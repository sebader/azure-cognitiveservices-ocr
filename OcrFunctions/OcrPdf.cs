using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage.Blob;
using OcrFunctions.Model;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace OcrFunctions
{
    public static class OcrPdf
    {
        private static IConfigurationRoot config = new ConfigurationBuilder()
        .SetBasePath(Environment.CurrentDirectory)
        .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
        .AddEnvironmentVariables()
        .Build();

        private static HttpClient _httpClient = null;

        private static readonly string[] allowedFileExtensions = new string[] { ".pdf", ".png", ".jpg", ".jpeg" };


        /// <summary>
        /// Azure Function that is triggerd by a new file in blob storage,
        /// sends the document to the Azure Cognitive Service for Optical Character Recognition (OCR)
        /// and stores the resulting text on the blob storage as well in .txt files
        /// </summary>
        /// <param name="inputBlob"></param>
        /// <param name="outputBlobContainer"></param>
        /// <param name="name"></param>
        /// <param name="log"></param>
        /// <returns></returns>
        [FunctionName("OcrPdf")]
        public static async Task Run(
            [BlobTrigger("ocr/input/{name}", Connection = "BlobConnectionString")]CloudBlockBlob inputBlob,
            [Blob("ocr", FileAccess.Read, Connection = "BlobConnectionString")] CloudBlobContainer outputBlobContainer,
            string name,
            ILogger log)
        {
            // Check if file extension is in the supported list
            if (!allowedFileExtensions.Contains(Path.GetExtension(name)?.ToLower()))
            {
                log.LogWarning($"Ignoring file {name} with unsupported file extension");
                return;
            }

            log.LogInformation($"New blob detected: '{name}'. Calling OCR for this document...");

            try
            {
                // Init http client
                if (_httpClient == null)
                {
                    _httpClient = new HttpClient();
                    // Add API key for Azure Cognitive Service in the header
                    _httpClient.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", config["CognitiveServiceApiKey"]);
                }

                // Generate a read-only, short living SAS token for the document to be passed to the OCR engine
                var sasPolicy = new SharedAccessBlobPolicy()
                {
                    Permissions = SharedAccessBlobPermissions.Read,
                    SharedAccessStartTime = new DateTimeOffset(DateTime.UtcNow),
                    SharedAccessExpiryTime = new DateTimeOffset(DateTime.UtcNow.AddMinutes(5))
                };
                var sasToken = inputBlob.GetSharedAccessSignature(sasPolicy);

                // Make Ocr Read request
                // This request is async and will only return an URL for us to retrieve the status and result of the request
                var resultCheckUrl = await MakeOCRRequest(inputBlob.Uri.ToString() + sasToken, log);

                // Check the status URL until we get a result or time out
                var ocrResult = await GetOcrRequestResult(resultCheckUrl, log);

                log.LogInformation($"Recognized text on {ocrResult.Pages.Length} pages");
                log.LogDebug(ocrResult.Text);

                var inputFilename = Path.GetFileNameWithoutExtension(name);
                var fileBaseName = $"{ inputFilename }_{ DateTime.UtcNow.ToString("yyyy-MM-ddThh-mm-ss") }";
                var outputFolder = $"output/{fileBaseName}/";

                // Add all pages as key-value pairs (Key=filename, Value=text) to new output dict
                var allPageFiles = ocrResult.Pages.ToDictionary(p => $"{fileBaseName}_page{ string.Format("{0:000}", p.PageNumber) }.txt",p => p.Text);

                // Add full output txt file to the list as well
                allPageFiles.Add($"{fileBaseName}_full.txt", ocrResult.Text);

                // Write all files to as blobs to storage
                foreach (var file in allPageFiles)
                {
                    var pageResultBlob = outputBlobContainer.GetBlockBlobReference(outputFolder + file.Key);
                    pageResultBlob.Properties.ContentType = "text/plain";
                    await pageResultBlob.UploadTextAsync(file.Value);
                }

                // In addition we can write all the text files into one zip file
                if (config["createResultZip"]?.ToLower() == "true")
                {
                    var zipFile = CreateZipFromTexts(allPageFiles);
                    var zipFileBlob = outputBlobContainer.GetBlockBlobReference($"{outputFolder + fileBaseName}_allPages.zip");
                    zipFileBlob.Properties.ContentType = "application/zip";
                    await zipFileBlob.UploadFromFileAsync(zipFile);
                    // Delete the temp zipfile from disk
                    File.Delete(zipFile);
                }

                log.LogInformation("Processing of document completed. Results uploaded to blob storage");
            }
            catch (Exception e)
            {
                log.LogError(e, $"Exception during processing. Cannot OCR document {name}");
            }
        }

        /// <summary>
        /// Gets the text visible in the specified image file by using
        /// the Computer Vision REST API.
        /// </summary>
        /// <param name="imageUrl">The image file URL</param>
        private static async Task<string> MakeOCRRequest(string imageUrl, ILogger log)
        {
            string uriReadAsync = config["cognitiveServiceBaseUri"] + "/vision/v2.0/read/core/asyncBatchAnalyze";

            // Request mode parameter. Can be "Handwritten" or "Printed"
            string requestParameters = "?mode=" + config["ocrMode"];

            // Assemble the URI for the REST API method.
            string uri = uriReadAsync + requestParameters;

            // Put the image URL into the JSON payload
            var requestJson = "{\"url\": \"" + imageUrl + "\" }";

            // Add the byte array as an octet stream to the request body.
            var content = new StringContent(requestJson);
            // This example uses the "aapplication/json" content type.
            // The other content types you can use are "application/octet-stream" if you actually post the image content
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            // Asynchronously call the REST API method.
            HttpResponseMessage response = await _httpClient.PostAsync(uri, content);

            response.EnsureSuccessStatusCode();
            // If the request was sucessfully accepted, the OCR service returns the status/result check URL in the
            // header Operation-Location
            if (response.Headers.TryGetValues("Operation-Location", out var values))
            {
                var opLoc = values.FirstOrDefault();
                return opLoc;
            }
            else
            {
                throw new Exception("No Operation-Location header returned");
            }
        }

        /// <summary>
        /// Call the status/result page for a previously scheduled OCR/Read operation
        /// </summary>
        /// <param name="statusUrl"></param>
        /// <param name="log"></param>
        /// <returns></returns>
        private static async Task<ReadResult> GetOcrRequestResult(string statusUrl, ILogger log)
        {
            // Give the service a little bit of lead time to get the result in the first pass
            await Task.Delay(2000);

            int counter = 0;
            while (counter < int.Parse(config["ocrMaxRetries"]))
            {
                // Asynchronously call the REST API method.
                HttpResponseMessage response = await _httpClient.GetAsync(statusUrl);
                response.EnsureSuccessStatusCode();

                var result = await response.Content.ReadAsAsync<ReadResult>();

                if (result?.status == "Succeeded")
                {
                    return result;
                }
                else if (result?.status == "Failed")
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

        /// <summary>
        /// Package all strings from a collection into text files in one zip archive
        /// </summary>
        /// <param name="texts"></param>
        /// <returns></returns>
        private static string CreateZipFromTexts(Dictionary<string, string> texts)
        {
            // create a temp file name for the zip file
            string zipFilePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            using (FileStream zipFile = new FileStream(zipFilePath, FileMode.CreateNew))
            {
                using (ZipArchive archive = new ZipArchive(zipFile, ZipArchiveMode.Create))
                {
                    foreach (var text in texts)
                    {
                        ZipArchiveEntry textFile = archive.CreateEntry(text.Key);
                        using (StreamWriter writer = new StreamWriter(textFile.Open()))
                        {
                            writer.Write(text.Value);
                        }
                    }
                }
            }
            return zipFilePath;
        }
    }
}