# Azure Function - OCR documents using Cognitive Services

This sample Azure Function is triggered by new documents being uploaded to a Blob Storage folder. Any suppored files (PDF, PNG, JPG) is then sent to the Azure Cognitive Service for OCR (Optical Character Recognition).
The result is being stored as txt files on the blob storage.
