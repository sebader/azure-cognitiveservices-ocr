# Azure Function - OCR documents using Cognitive Services

This sample Azure Function is triggered by new documents being uploaded to a Blob Storage folder. Any suppored files (PDF, PNG, JPG) is then sent to the Azure Cognitive Service for OCR (Optical Character Recognition).
The result is being stored as txt files on the blob storage.

This sample requires a previously created Cognitive Services resource in Azure: https://azure.microsoft.com/en-us/services/cognitive-services/computer-vision/

To run the Function, deploy it to an Azure Function app and set the following application settings:
- `cognitiveServiceBaseUri`: Base URL of your cognitive service resource. Sample: `https://westeurope.api.cognitive.microsoft.com`
- `CognitiveServiceApiKey`: API key for the aforementioned Cognitive Service: Sample: `1b***b3dc1****b8f8*****db**65`
- `BlobConnectionString`: Connection string to the blob storage where you upload the input images and where the result text files will be stored. Sample: `DefaultEndpointsProtocol=https;AccountName=myocrblob;AccountKey=***********;EndpointSuffix=core.windows.net`
- `ocrRetrySeconds`: How many seconds the function waits between each retry to fetch OCR result. Sample: `5`
- `ocrMaxRetries`: How many retries the Function does to fetch OCR results before timing out. Sample: `10`
- `ocrMode`: Type of input documents. Can either be `Handwritten`or `Printed`

*Note: Currently the [OCR API](https://westus.dev.cognitive.microsoft.com/docs/services/5adf991815e1060e6355ad44/operations/2afb498089f74080d7ef85eb) that is being used in this sample only supports English documents. More languages will follow soon.*
