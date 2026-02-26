using RestSharp;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MiMotionSign
{
    public static class SendUtil
    {
        public static int SendEMail(string smtp_Server, int smtp_Port, string smtp_Email, string smtp_Password, List<string> receive_Email_List, string title, string content, string topicName)
        {
            if (string.IsNullOrWhiteSpace(smtp_Email) || string.IsNullOrWhiteSpace(smtp_Password) || receive_Email_List == null || receive_Email_List.Count == 0 || receive_Email_List.All(string.IsNullOrWhiteSpace))
            {
                Console.WriteLine("【EMail】RECEIVE_EMAIL_LIST is null");
                return 0;
            }

            MailAddress fromMail = new(smtp_Email, topicName);
            foreach (var item in receive_Email_List)
            {
                if (string.IsNullOrWhiteSpace(item))
                    continue;

                MailAddress toMail = new(item);

                MailMessage mail = new(fromMail, toMail)
                {
                    IsBodyHtml = false,
                    Subject = title,
                    Body = content
                };

                SmtpClient client = new()
                {
                    EnableSsl = true,
                    Host = smtp_Server,
                    Port = smtp_Port,
                    UseDefaultCredentials = false,
                    Credentials = new NetworkCredential(smtp_Email, smtp_Password),
                    DeliveryMethod = SmtpDeliveryMethod.Network
                };

                client.Send(mail);
            }

            Console.WriteLine("【EMail】Success");
            return 1;
        }

        public static async Task<int> SendBark(string bark_Devicekey, string bark_Icon, string title, string content)
        {
            if (string.IsNullOrWhiteSpace(bark_Devicekey))
            {
                Console.WriteLine("【Bark】BARK_DEVICEKEY is empty");
                return 0;
            }

            string url = "https://api.day.app/push";
            if (string.IsNullOrWhiteSpace(bark_Icon) == false)
                url = url + "?icon=" + bark_Icon;

            Dictionary<string, string> headers = new()
            {
                { "charset", "utf-8" }
            };

            Dictionary<string, object> param = new()
            {
                { "title", title },
                { "body", content },
                { "device_key", bark_Devicekey }
            };

            var client = new RestClient(url);
            RestRequest request = new() { Method = Method.Post };
            request.AddOrUpdateHeaders(headers);
            request.AddHeader("Content-Type", "application/json");
            var body = param.ToJson();
            request.AddStringBody(body, DataFormat.Json);
            RestResponse response = await client.ExecuteAsync(request);
            var res = response.Content;
            var jObject = res.TryToObject<JsonObject>();
            try
            {
                if (jObject == null)
                {
                    Console.WriteLine("【Bark】Send message to Bark Error");
                    return -1;
                }
                else
                {
                    if (int.TryParse(jObject["code"]?.ToString(), out int code) && code == 200)
                    {
                        Console.WriteLine("【Bark】Send message to Bark successfully");
                        return 1;
                    }
                    else
                    {
                        Console.WriteLine($"【Bark】Send Message Response.{jObject["text"]?.ToString()}");
                        return 0;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("【Bark】Send message to Bark Catch." + (ex?.Message ?? ""));
                return -1;
            }
        }
    }

    public static class Util
    {
        public static string DesensitizeStr(string str)
        {
            if (string.IsNullOrWhiteSpace(str))
                return "";

            if (str.Length <= 8)
            {
                int ln = Math.Max((int)Math.Floor((double)str.Length / 3), 1);
                return str[..ln] + "**" + str[^ln..];
            }

            return str[..3] + "**" + str[^4..];
        }

        public static long GetTimeStamp_Seconds()
        {
            DateTime currentTime = DateTime.UtcNow;
            DateTime unixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            TimeSpan elapsedTime = currentTime - unixEpoch;
            return (long)elapsedTime.TotalSeconds;
        }

        public static long GetTimeStamp_Milliseconds()
        {
            DateTime currentTime = DateTime.UtcNow;
            DateTime unixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            TimeSpan elapsedTime = currentTime - unixEpoch;
            return (long)elapsedTime.TotalMilliseconds;
        }

        public static string GetFakeIP()
        {
            Random rd = new(Guid.NewGuid().GetHashCode());
            return $"233.{rd.Next(64, 117)}.{rd.Next(0, 255)}.{rd.Next(0, 255)}";
        }

        public static DateTime GetBeiJingTime()
        {
            DateTime nowUtc = DateTime.UtcNow;
            TimeZoneInfo beijingTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
            DateTime nowBeiJing = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, beijingTimeZone);
            return nowBeiJing;
        }

        public static string GetBeiJingTimeStr()
        {
            var dt = GetBeiJingTime();
            return dt.ToString("yyyy-MM-dd HH:mm:ss");
        }

        public static string BodyUrlEncode(Dictionary<string, string> parameters) => string.Join("&", parameters.Select(p => WebUtility.UrlEncode(p.Key) + "=" + WebUtility.UrlEncode(p.Value)));

        public static T ToObject<T>(this string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return default;

            return string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            });
        }

        public static T TryToObject<T>(this string json)
        {
            try
            {
                return json.ToObject<T>();
            }
            catch
            {
                return default;
            }
        }

        public static string ToJson(this object obj)
        {
            var options = new JsonSerializerOptions
            {
                Converters =
                {
                    new DateTimeConverterUsingDateTimeFormat("yyyy-MM-dd HH:mm:ss")
                }
            };

            return JsonSerializer.Serialize(obj, options);
        }

        public static string GetEnvValue(string key)
        {
            string str = Environment.GetEnvironmentVariable(key);

#if DEBUG
            if (string.IsNullOrWhiteSpace(str))
            {

            }
#endif

            return str;
        }

        public static string GetExceptionMsg(Exception ex, string backStr)
        {
            StringBuilder sb = new();
            //sb.AppendLine("");
            //sb.AppendLine("****************************Exception-Start****************************");
            if (ex != null)
            {
                //sb.AppendLine("【异常类型】：" + ex.GetType().Name);
                //sb.AppendLine("【异常信息】：" + ex.Message);
                //sb.AppendLine("【堆栈调用】：" + ex.StackTrace);
            }
            else
            {
                //sb.AppendLine("【未处理异常】：" + backStr);
            }
            //sb.AppendLine("****************************Exception-End****************************");
            //sb.AppendLine("");
            return sb.ToString();
        }

        public static int ToInt(this string obj, int defaultValue)
        {
            if (string.IsNullOrEmpty(obj) == false && int.TryParse(obj, out int n))
                return n;
            else
                return defaultValue;
        }

        public static void SetEnvValue(string key, string value)
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    public class DateTimeConverterUsingDateTimeFormat : JsonConverter<DateTime>
    {
        private readonly string _format;

        public DateTimeConverterUsingDateTimeFormat(string format)
        {
            _format = format;
        }

        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return DateTime.ParseExact(reader.GetString(), _format, null);
        }

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString(_format));
        }
    }

    public class SendConf
    {
        public string Bark_Devicekey { get; set; }
        public string Bark_Icon { get; set; }
        public string Smtp_Server { get; set; }
        public int Smtp_Port { get; set; }
        public string Smtp_Email { get; set; }
        public string Smtp_Password { get; set; }
        public List<string> Receive_Email_List { get; set; }
    }
}
