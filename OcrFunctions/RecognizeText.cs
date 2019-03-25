using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.CognitiveServices.Vision.ComputerVision;
using Microsoft.Azure.CognitiveServices.Vision.ComputerVision.Models;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage.Blob;

namespace OcrFunctions
{
    public static class RecognizeText
    {

        private static IConfigurationRoot config = new ConfigurationBuilder()
        .SetBasePath(Environment.CurrentDirectory)
        .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
        .AddEnvironmentVariables()
        .Build();

        private static ComputerVisionClient cvClient = new ComputerVisionClient(new ApiKeyServiceClientCredentials(config["CognitiveServiceApiKey"]))
        {
            Endpoint = config["cognitiveServiceBaseUri"]
        };

        private static readonly string[] allowedFileExtensions = new string[] { ".pdf", ".png", ".jpg", ".jpeg" };
        [FunctionName("RecognizeText")]
        public static async Task Run(
            [BlobTrigger("ocr/input/{name}", Connection = "BlobConnectionString")]CloudBlockBlob inputBlob,
            [Blob("ocr", FileAccess.Read, Connection = "BlobConnectionString")] CloudBlobContainer outputBlobContainer,
            string name,
            ILogger log)
        {
            // Generate a read-only, short living SAS token for the document to be passed to the OCR engine
            var sasPolicy = new SharedAccessBlobPolicy()
            {
                Permissions = SharedAccessBlobPermissions.Read,
                SharedAccessStartTime = new DateTimeOffset(DateTime.UtcNow),
                SharedAccessExpiryTime = new DateTimeOffset(DateTime.UtcNow.AddMinutes(5))
            };
            var sasToken = inputBlob.GetSharedAccessSignature(sasPolicy);


            var response = await cvClient.BatchReadFileAsync(inputBlob.Uri.ToString() + sasToken, TextRecognitionMode.Printed);

            var result = await GetReadOperationResult(response.OperationLocation, log);

            var inputFilename = Path.GetFileNameWithoutExtension(name);
            var fileBaseName = $"{ inputFilename }_{ DateTime.UtcNow.ToString("yyyy-MM-ddThh-mm-ss") }";
            var outputFolder = $"output/{fileBaseName}/";

            // Add all pages as key-value pairs (Key=filename, Value=text) to new output dict
            var allPageFiles = result.RecognitionResults.ToDictionary(p => $"{fileBaseName}_page{ string.Format("{0:000}", p.Page) }.txt", p => GetTextOnPage(p));

            // Add full output txt file to the list as well
            allPageFiles.Add($"{fileBaseName}_full.txt", string.Join("\n\n\n", allPageFiles.Select(p => p.Value)));

            // Write all files to as blobs to storage
            foreach (var file in allPageFiles)
            {
                var pageResultBlob = outputBlobContainer.GetBlockBlobReference(outputFolder + file.Key);
                pageResultBlob.Properties.ContentType = "text/plain";
                await pageResultBlob.UploadTextAsync(file.Value);
            }
        }

        /// <summary>
        /// Call the status/result page for a previously scheduled OCR/Read operation
        /// </summary>
        /// <param name="statusUrl"></param>
        /// <param name="log"></param>
        /// <returns></returns>
        private static async Task<ReadOperationResult> GetReadOperationResult(string operationLocation, ILogger log)
        {
            // Give the service a little bit of lead time to get the result in the first pass
            await Task.Delay(2000);

            int counter = 0;
            while (counter < int.Parse(config["ocrMaxRetries"]))
            {

                var result = await cvClient.GetReadOperationResultAsync(operationLocation.Substring(operationLocation.LastIndexOf('/') + 1));

                if (result?.Status == TextOperationStatusCodes.Succeeded)
                {
                    return result;
                }
                else if (result?.Status == TextOperationStatusCodes.Failed)
                {
                    throw new Exception("Document could not be OCRed. Engine returned 'Failed'");
                }
                else
                {
                    counter++;
                    if (counter >= int.Parse(config["ocrMaxRetries"]))
                        break;

                    log.LogInformation("Waiting for OCR to finish ...");
                    // Wait for X seconds and try again
                    await Task.Delay(int.Parse(config["ocrRetrySeconds"]) * 1000);
                }
            }
            throw new Exception($"Document could not be OCRed before timeout after {counter} retries'");
        }

        private static string GetTextOnPage(TextRecognitionResult page)
        {
            StringBuilder sb = new StringBuilder();
            float? lastTop = null;
            foreach (Line l in page.Lines)
            {
                if (lastTop != null && (l.BoundingBox[1] - lastTop >= 0.2))
                {
                    sb.Append("\n");
                }
                else if (sb.Length > 0)
                {
                    sb.Append(" ");
                }
                sb.Append(l.Text);
                lastTop = l.BoundingBox[1];
            }
            return sb.ToString();
        }
    }
}
