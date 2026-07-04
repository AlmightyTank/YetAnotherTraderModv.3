using Newtonsoft.Json;

namespace YetAnotherTraderMod.Client.Services
{
    public static class JsonFileUtil
    {
        public static T Deserialize<T>(string json)
        {
            return JsonConvert.DeserializeObject<T>(json);
        }

        public static string Serialize<T>(T value)
        {
            return JsonConvert.SerializeObject(value, Formatting.Indented);
        }
    }
}
