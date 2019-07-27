using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Documents;
using Microsoft.Azure.Documents.Client;
using Microsoft.Extensions.Logging;
using ToKindleAzure.Models;
using Microsoft.Extensions.Configuration;


namespace ToKindleAzure
{
    public static class Function1
    {
//#if DEBUG
//        private const string Kindlegen = @"/kindlegen/kindlegen.exe";
//#else
        private const string Kindlegen = @"d:\home\site\wwwroot\kindlegen\kindlegen.exe";
//#endif

        private const string DatabaseId = "kindle";
        private const string CollectionId = "kindle-urls";


        [FunctionName("sendToKindle")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req,
            [CosmosDB(
                databaseName: "Kindle",
                collectionName: "Urls",
                ConnectionStringSetting = "CosmosDBConnection")] DocumentClient client,
            ILogger log)
        {

            string link = req.Form["link"];
            string html = req.Form["html"];
            string title = req.Form["title"];
            bool reSend = Boolean.Parse(req.Form["reSend"]);

            if (Environment.GetEnvironmentVariable("CreateDatabase") == null || Environment.GetEnvironmentVariable("CosmosDBConnection") == null)
                return new BadRequestObjectResult("no setting found for CreateDatabase and CosmosDBConnection environment variables, set this setting in azure function application settings.");

            if (string.IsNullOrEmpty(html) || string.IsNullOrEmpty(link) || string.IsNullOrEmpty(title)) return new NoContentResult();

            if (!reSend && await CheckList(client, link))
            {
                return new StatusCodeResult((int)HttpStatusCode.AlreadyReported);
            }

            return ProcessContent(link, title, html, reSend);
        }

        public static IActionResult ProcessContent(string link, string title, string html, bool reSend)
        {
            try
            {

                var fileName = Path.GetInvalidFileNameChars().Aggregate(title, (current, c) => current.Replace(c.ToString(), "-"));
                string path = Path.Combine(TempFolder, fileName + ".html");

                var writer = new FileStream(path, FileMode.Create);
                writer.Dispose();

                File.WriteAllText(path, html);

                WriteToStreamAsync(fileName);
                return new OkResult();
            }
            catch (Exception e)
            {
                return new BadRequestObjectResult(e.Message);
            }

        }
        private static async Task<bool> CheckList(IDocumentClient client, string link)
        {
            var CreateDatabase = Environment.GetEnvironmentVariable("CreateDatabase");

            if (CreateDatabase != null && CreateDatabase == "true")
            {
                var databaseDefinition = new Database { Id = DatabaseId };
                await client.CreateDatabaseIfNotExistsAsync(databaseDefinition);

                var collectionDefinition = new DocumentCollection { Id = CollectionId };
                await client.CreateDocumentCollectionIfNotExistsAsync(UriFactory.CreateDatabaseUri(DatabaseId),
                    collectionDefinition);
            }

            var response = client.CreateDocumentQuery<Item>(UriFactory.CreateDocumentCollectionUri(DatabaseId, CollectionId), $"select * from c WHERE  c.Url='{link}'").ToList();

            if (response.Count > 0)
                return true;

            await client.CreateDocumentAsync(
                UriFactory.CreateDocumentCollectionUri(DatabaseId, CollectionId),
                new Item() { Url = link });

            return false;
        }
        private static void WriteToStreamAsync(string fileName)
        {
            var tcs = new TaskCompletionSource<object>();

//            if (!File.Exists(Kindlegen)) throw new Exception("not kindlegen file");

            var filepath = Path.Combine(TempFolder, fileName + ".html");
            var mobipath = Path.Combine(TempFolder, fileName + ".mobi");

            var lines = new StringBuilder();

            if (!File.Exists(filepath)) throw new Exception("not html file");

            if (!File.Exists(mobipath))
            {
                var kindleGen = new Process
                {
                    StartInfo =
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        FileName = Kindlegen,
                        Arguments = string.Format(@"""{0}""", filepath)
                    }
                };
                kindleGen.Start();
                while (!kindleGen.StandardOutput.EndOfStream)
                {
                    string line = kindleGen.StandardOutput.ReadLine();
                    if (string.IsNullOrEmpty(line) || line.Contains("Hyperlink not resolved:"))
                        continue;
                    lines.AppendLine(line);
                }
                kindleGen.WaitForExit();
            }

            tcs.SetResult(null);
            if (File.Exists(mobipath))
                SendToKindle(mobipath);
            if (File.Exists(filepath))
                File.Delete(filepath);
            if (File.Exists(mobipath))
                File.Delete(mobipath);
        }



        static void SendToKindle(string file)
        {
            var emailFrom = Environment.GetEnvironmentVariable("emailFrom");
            var emailFromPass = Environment.GetEnvironmentVariable("emailFromPass");
            var emailTo = Environment.GetEnvironmentVariable("emailTo");

            if(emailFrom==null||emailFromPass==null||emailTo==null)
                throw new Exception("you must set environment variables 'emailFrom','emailFromPass','emailTo', set this variables in azure function application settings or for debugging in local.settings.json. ");

            var fromAddress = new MailAddress(emailFrom);
            var toAddress = new MailAddress(emailTo);
            const string subject = "From Chrome ToKindle APP";
            const string body = "From Chrome ToKindle APP";
            var smtp = new SmtpClient
            {
                Host = "smtp.gmail.com",
                Port = 587,
                EnableSsl = true,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                UseDefaultCredentials = false,
                Credentials = new NetworkCredential(fromAddress.Address, emailFromPass)
            };
            // Create  the file attachment for this e-mail message.
            var data = new System.Net.Mail.Attachment(file, MediaTypeNames.Application.Octet);
            // Add time stamp information for the file.
            var disposition = data.ContentDisposition;
            disposition.CreationDate = File.GetCreationTime(file);
            disposition.ModificationDate = File.GetLastWriteTime(file);
            disposition.ReadDate = File.GetLastAccessTime(file);

            using (var message = new MailMessage(fromAddress, toAddress)
            {
                Subject = subject,
                Body = body,
                Attachments = { data }
            })
            {
                smtp.Send(message);
            }
        }

        private static string TempFolder => Path.GetTempPath();
    }
}
