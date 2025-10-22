using Newtonsoft.Json;

namespace Bonis.Models.OneDataResponse
{
    public class OneDataResponse : OneDataResponse<dynamic>
    {
        public override void Deserialize()
        {
        }
    }

    public abstract class OneDataResponse<T>
    {

        [JsonProperty("continuationToken")]
        public string ContinuationToken { get; set; }
        [JsonProperty("rows")]
        public dynamic[] Rows { get; set; }
        public dynamic[] Columns { get; set; }
        public List<T> Items { get; set; }
        public abstract void Deserialize();

    }
}
