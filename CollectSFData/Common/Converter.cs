using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace CollectSFData.Common
{
    public class Converter<T> : JsonConverter<T>
    {
        public override T ReadJson(JsonReader reader, Type objectType, T existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            throw new NotImplementedException();
        }

        public override void WriteJson(JsonWriter writer, T value, JsonSerializer serializer)
        {
            if (CanConvert(typeof(T)))
            {
                serializer.Serialize(writer, value);
            }
            else
            {
                Log.Warning($"unable to serialize {typeof(T)}");
            }
            

            //writer.WriteValue(value);
            

        }
    }
}
