using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage.Blob;

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


        [FunctionName("OcrPdf")]
        public static async Task Run(
            [BlobTrigger("ocr/input/{name}", Connection = "BlobConnectionString")]CloudBlockBlob myBlob,
            [Blob("ocr/output/{name}_{DateTime}.txt", FileAccess.Write, Connection = "BlobConnectionString")] CloudBlockBlob resultTextFileBlob,
            string name,
            ILogger log)
        {
            log.LogInformation($"New blob detected: '{name}'. Calling OCR for this document...");

            try
            {
                // Generate a read-only SAS token for the document to be passed to the OCR engine
                var sasPolicy = new SharedAccessBlobPolicy()
                {
                    Permissions = SharedAccessBlobPermissions.Read,
                    SharedAccessStartTime = new DateTimeOffset(DateTime.UtcNow),
                    SharedAccessExpiryTime = new DateTimeOffset(DateTime.UtcNow.AddMinutes(10))
                };
                var sasToken = myBlob.GetSharedAccessSignature(sasPolicy);

                // Init http client
                if (_httpClient == null)
                {
                    _httpClient = new HttpClient();
                    _httpClient.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", config["CognitiveServiceApiKey"]);
                }

                // Make Ocr Read request
                // This request is async and will only return an URL for us to retrieve the status and result of the request
                var resultCheckUrl = await MakeOCRRequest(myBlob.Uri.ToString() + sasToken, log);

                // Check the status URL until we get a result or time out
                var ocrResult = await GetOcrRequestResult(resultCheckUrl, log);

                log.LogInformation("Recognized Text:");
                log.LogInformation(ocrResult.Text);

                // Write output txt file to blob storage
                await resultTextFileBlob.UploadTextAsync(ocrResult.Text);
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
            string requestParameters = "mode=" + config["ocrMode"];

            // Assemble the URI for the REST API method.
            string uri = uriReadAsync + "?" + requestParameters;

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
                var opLoc = values.First();
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

                    log.LogInformation("Waiting for OCR to finished...");
                    // Wait for X seconds and try again
                    await Task.Delay(int.Parse(config["ocrRetrySeconds"]) * 1000);
                }
            }
            throw new Exception($"Document could not be OCRed before timeout after {counter} retries'");
        }
    }


    /// <summary>
    /// The following classes are the DTO of the OCR response
    /// </summary>
    public class ReadResult
    {
        public string status { get; set; }
        public PageRecognitionResult[] recognitionResults { get; set; }

        /// <summary>
        /// Get text for all pages. Seperated by 3 new lines
        /// </summary>
        public string Text
        {
            get
            {
                return string.Join("\n\n\n", recognitionResults?.Select(r => r.Text));
            }
        }
    }

    public class PageRecognitionResult
    {
        public int page { get; set; }
        public float clockwiseOrientation { get; set; }
        public float width { get; set; }
        public float height { get; set; }
        public string unit { get; set; }
        public Line[] lines { get; set; }

        /// <summary>
        /// Get text of the entire page. Tries to align lines if they only slightly differ in vertical position into one line.
        /// </summary>
        public string Text
        {
            get
            {
                StringBuilder sb = new StringBuilder();
                float? lastTop = null;
                foreach (Line l in lines)
                {
                    if (lastTop != null && (l.boundingBox[1] - lastTop >= 0.2))
                    {
                        sb.Append("\n");
                    }
                    else
                    {
                        sb.Append(" ");
                    }
                    sb.Append(l.text);
                    lastTop = l.boundingBox[1];
                }

                return sb.ToString();
            }
        }
    }

    public class Line
    {
        public float[] boundingBox { get; set; }
        public string text { get; set; }
        public Word[] words { get; set; }
    }

    public class Word
    {
        public float[] boundingBox { get; set; }
        public string text { get; set; }
        public string confidence { get; set; }
    }

}
