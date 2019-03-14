using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace OcrFunctions.Model
{
    /// <summary>
    /// The following classes are the DTO of the OCR response
    /// </summary>
    public class ReadResult
    {
        public string status { get; set; }

        [JsonProperty("recognitionResults")]
        public PageRecognitionResult[] Pages { get; set; }

        /// <summary>
        /// Get text for all pages. Seperated by 3 new lines
        /// </summary>
        public string Text
        {
            get
            {
                return string.Join("\n\n\n", Pages?.Select(r => r.Text));
            }
        }
    }

    public class PageRecognitionResult
    {
        [JsonProperty("page")]
        public int PageNumber { get; set; }
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
                    else if (sb.Length > 0)
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