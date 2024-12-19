using RestSharp;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MiMotionSign
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            var dt1 = GetBeiJingTime();
            Console.WriteLine(dt1.ToString("yyyy-MM-dd HH:mm:ss") + "-Start running...");

            await Run();

            var dt2 = GetBeiJingTime();
            Console.WriteLine(dt2.ToString("yyyy-MM-dd HH:mm:ss") + "-End running...");
        }

        static async Task Run()
        {
            Conf conf = Util.Deserialize<Conf>(Util.GetEnvValue("CONF"));
            if (conf == null)
            {
                Console.WriteLine("Configuration initialization failed");
                return;
            }

            if (conf.Peoples == null)
            {
                Console.WriteLine("The list is empty");
                return;
            }

            int sleepGapSecond = 6;
            if (conf.Sleep_Gap_Second > 0)
                sleepGapSecond = conf.Sleep_Gap_Second;

            List<QueueModel> queueModels = [];
            foreach (var people in conf.Peoples.Where(x => string.IsNullOrWhiteSpace(x.User) == false && string.IsNullOrWhiteSpace(x.Pwd) == false))
            {
                string user = people.User;
                string password = people.Pwd;
                if (user.Contains("+86") || user.Contains('@'))
                    user = people.User;
                else
                    user = "+86" + people.User;
                bool isPhone = user.Contains("+86");
                string fakeIP = GetFakeIP();

                int min = people.MinStep;
                int max = people.MaxStep;
                if (min <= 0)
                    min = 18000;
                if (max <= 0)
                    max = 25000;

                DateTime nowBeiJing = GetBeiJingTime();

                int hour = nowBeiJing.Hour;
                int minute = nowBeiJing.Minute;
                var time_rate = Math.Min((hour * 60 * 1.0 + minute) / (24 * 60), 1);
                min = (int)(min * time_rate);
                max = (int)(max * time_rate);

                queueModels.Add(new QueueModel()
                {
                    User = user,
                    Pwd = password,
                    MinStep = min,
                    MaxStep = max,
                    IsPhone = isPhone,
                    FakeIP = fakeIP,
                    NowBeiJing = nowBeiJing,
                });
            }

            List<QueueResult> results = [];
            if (conf.UseConcurrent)
                results = await Concurrent_Run(queueModels);
            else
                results = await Sequence_Run(queueModels, sleepGapSecond);

            List<string> message_all = ["当前运行模式：【" + (conf.UseConcurrent ? "并行" : $"顺序执行，间隔 {sleepGapSecond} 秒左右") + "】"];

            for (int i = 0; i < queueModels.Count; i++)
            {
                var people = queueModels[i];

                string current = i + 1 + "、" + DesensitizeUserName(people.User);
                message_all.Add(current);
                Console.WriteLine(current);

                var currentResult = results?.FirstOrDefault(x => x.User == people.User);
                bool success = currentResult?.Success ?? false;
                string step = currentResult?.Step ?? "";
                string msg = currentResult?.Msg ?? "未知";
                if (success)
                {
                    message_all.Add("    操作成功：" + step + $"，范围：{people.MinStep}~{people.MaxStep}");
                    Console.WriteLine("    success：" + step + $"，Range：{people.MinStep}~{people.MaxStep}");
                }
                else
                {
                    message_all.Add("    失败：" + msg);
                    Console.WriteLine("    error：" + msg);
                }
            }

            string title = "刷步数提醒";
            string content = string.Join("\n", message_all);
            string topicName = "MiMotion Remind Services";

            Console.WriteLine("Send");
            SendUtil.SendEMail(conf.Smtp_Server, conf.Smtp_Port, conf.Smtp_Email, conf.Smtp_Password, conf.Receive_Email_List, title, content, topicName);
            await SendUtil.SendBark(conf.Bark_Devicekey, conf.Bark_Icon, title, content);
        }

        static async Task<List<QueueResult>> Sequence_Run(List<QueueModel> queueModels, int sleepGapSecond)
        {
            List<QueueResult> results = [];
            for (int i = 0; i < queueModels.Count; i++)
            {
                var currentResult = await Run_Single(queueModels[i]);
                results.Add(currentResult);

                if (i < (queueModels.Count - 1))
                {
                    Random rd = new(Guid.NewGuid().GetHashCode());
                    await Task.Delay(rd.Next(sleepGapSecond * 1000 - 2143, sleepGapSecond * 1000 + 2143));
                }
            }
            return results;
        }

        static async Task<List<QueueResult>> Concurrent_Run(List<QueueModel> queueModels)
        {
            Task<QueueResult>[] tasks = new Task<QueueResult>[queueModels.Count];
            for (int i = 0; i < tasks.Length; i++)
            {
                tasks[i] = Run_Single(queueModels[i]);
            }
            QueueResult[] results = await Task.WhenAll(tasks);
            return results.ToList();
        }

        static async Task<QueueResult> Run_Single(QueueModel queueModel)
        {
            try
            {
                (bool success, string step, string msg) = await Motion_LoginAndStep(queueModel.User, queueModel.Pwd, queueModel.IsPhone, queueModel.FakeIP, queueModel.MinStep, queueModel.MaxStep, queueModel.NowBeiJing);
                return new QueueResult() { User = queueModel.User, Success = success, Step = step, Msg = msg, };
            }
            catch (Exception ex)
            {
                return new QueueResult() { User = queueModel.User, Success = false, Step = "", Msg = ex?.Message, };
            }
        }

        static async Task<(string login_token, string userid, string error)> Motion_Login(string user, string password, bool isPhone, string fakeIP)
        {
            var url1 = "https://api-user.huami.com/registrations/" + user + "/tokens";
            Dictionary<string, string> login_headers = new()
            {
                { "Content-Type", "application/x-www-form-urlencoded;charset=UTF-8" },
                { "User-Agent", "Mozilla/5.0 (iPhone; CPU iPhone OS 14_7_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/14.1.2" },
                { "X-Forwarded-For", fakeIP },
            };
            Dictionary<string, string> data1 = new()
            {
                { "client_id", "HuaMi" },
                { "password", password },
                { "redirect_uri", "https://s3-us-west-2.amazonaws.com/hm-registration/successsignin.html" },
                { "token", "access" },
            };

            string code = string.Empty;
            try
            {
                var client1 = new RestClient(url1, options => { options.FollowRedirects = false; });
                RestRequest request1 = new() { Method = Method.Post };
                request1.AddOrUpdateHeaders(login_headers);
                foreach (var item in data1)
                    request1.AddOrUpdateParameter(item.Key, item.Value);
                RestResponse response1 = await client1.ExecuteAsync(request1);
                if (((int)response1.StatusCode) != 303)
                    return ("", "", $"登录异常，status_code:{(int)response1.StatusCode}");

                string location = response1.Headers.Where(x => x.Name == "Location").FirstOrDefault()?.Value ?? "";

                code = GetAccessToken(location);
                if (string.IsNullOrWhiteSpace(code))
                    return ("", "", $"获取accessToken失败");
            }
            catch (Exception ex)
            {
                return ("", "", $"获取accessToken异常:{ex.Message}");
            }

            var url2 = "https://account.huami.com/v2/client/login";
            Dictionary<string, string> data2 = [];
            if (isPhone)
            {
                data2 = new()
                {
                    {"app_name", "com.xiaomi.hm.health" },
                    {"app_version", "4.6.0" },
                    {"code", code },
                    {"country_code", "CN" },
                    {"device_id", "2C8B4939-0CCD-4E94-8CBA-CB8EA6E613A1" },
                    {"device_model", "phone" },
                    {"grant_type", "access_token" },
                    {"third_name", "huami_phone" },
                };
            }
            else
            {
                data2 = new()
                {
                    {"allow_registration=", "false" },
                    {"app_name", "com.xiaomi.hm.health" },
                    {"app_version", "6.3.5" },
                    {"code", code },
                    {"country_code", "CN" },
                    {"device_id", "2C8B4939-0CCD-4E94-8CBA-CB8EA6E613A1" },
                    {"device_model", "phone" },
                    {"dn", "api-user.huami.com%2Capi-mifit.huami.com%2Capp-analytics.huami.com" },
                    {"grant_type", "access_token" },
                    {"lang", "zh_CN" },
                    {"os_version", "1.5.0" },
                    {"source", "com.xiaomi.hm.health" },
                    {"third_name", "email" },
                };
            }

            var client2 = new RestClient(url2);
            RestRequest request2 = new() { Method = Method.Post };
            request2.AddOrUpdateHeaders(login_headers);
            foreach (var item in data2)
                request2.AddOrUpdateParameter(item.Key, item.Value);
            RestResponse response2 = await client2.ExecuteAsync(request2);
            var jObject2 = JsonSerializer.Deserialize<JsonObject>(response2.Content);

            var login_token = jObject2["token_info"]["login_token"]?.ToString() ?? "";
            var userid = jObject2["token_info"]["user_id"]?.ToString() ?? "";

            return (login_token, userid, "");
        }

        static async Task<string> Motion_GetAppToken(string login_token, string fakeIP)
        {
            var url = $"https://account-cn.huami.com/v1/client/app_tokens?app_name=com.xiaomi.hm.health&dn=api-user.huami.com%2Capi-mifit.huami.com%2Capp-analytics.huami.com&login_token={login_token}";
            Dictionary<string, string> login_headers = new()
            {
                { "User-Agent", "MiFit/5.3.0 (iPhone; iOS 14.7.1; Scale/3.00)" },
                { "X-Forwarded-For", fakeIP },
            };
            var client1 = new RestClient(url);
            RestRequest request1 = new() { Method = Method.Get };
            request1.AddOrUpdateHeaders(login_headers);
            RestResponse response1 = await client1.ExecuteAsync(request1);
            var jObject = JsonSerializer.Deserialize<JsonObject>(response1.Content);
            var app_token = jObject["token_info"]["app_token"]?.ToString() ?? "";

            return app_token;
        }

        static async Task<(bool success, string step, string msg)> Motion_LoginAndStep(string user, string password, bool isPhone, string fakeIP, int min, int max, DateTime nowBeiJing)
        {
            Random rd = new(Guid.NewGuid().GetHashCode());

            (string login_token, string userid, string error) = await Motion_Login(user, password, isPhone, fakeIP);
            if (string.IsNullOrWhiteSpace(error) == false)
                return (false, "", error);
            if (string.IsNullOrWhiteSpace(login_token))
                return (false, "", "登录失败");

            string today = nowBeiJing.ToString("yyyy-MM-dd");
            string step = rd.Next(min, max).ToString();
            var data_json = "%5B%7B%22data_hr%22%3A%22%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F9L%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2FVv%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F0v%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F9e%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F0n%5C%2Fa%5C%2F%5C%2F%5C%2FS%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F0b%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F1FK%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2FR%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F9PTFFpaf9L%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2FR%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F0j%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F9K%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2FOv%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2Fzf%5C%2F%5C%2F%5C%2F86%5C%2Fzr%5C%2FOv88%5C%2Fzf%5C%2FPf%5C%2F%5C%2F%5C%2F0v%5C%2FS%5C%2F8%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2FSf%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2Fz3%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F0r%5C%2FOv%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2FS%5C%2F9L%5C%2Fzb%5C%2FSf9K%5C%2F0v%5C%2FRf9H%5C%2Fzj%5C%2FSf9K%5C%2F0%5C%2F%5C%2FN%5C%2F%5C%2F%5C%2F%5C%2F0D%5C%2FSf83%5C%2Fzr%5C%2FPf9M%5C%2F0v%5C%2FOv9e%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2FS%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2Fzv%5C%2F%5C%2Fz7%5C%2FO%5C%2F83%5C%2Fzv%5C%2FN%5C%2F83%5C%2Fzr%5C%2FN%5C%2F86%5C%2Fz%5C%2F%5C%2FNv83%5C%2Fzn%5C%2FXv84%5C%2Fzr%5C%2FPP84%5C%2Fzj%5C%2FN%5C%2F9e%5C%2Fzr%5C%2FN%5C%2F89%5C%2F03%5C%2FP%5C%2F89%5C%2Fz3%5C%2FQ%5C%2F9N%5C%2F0v%5C%2FTv9C%5C%2F0H%5C%2FOf9D%5C%2Fzz%5C%2FOf88%5C%2Fz%5C%2F%5C%2FPP9A%5C%2Fzr%5C%2FN%5C%2F86%5C%2Fzz%5C%2FNv87%5C%2F0D%5C%2FOv84%5C%2F0v%5C%2FO%5C%2F84%5C%2Fzf%5C%2FMP83%5C%2FzH%5C%2FNv83%5C%2Fzf%5C%2FN%5C%2F84%5C%2Fzf%5C%2FOf82%5C%2Fzf%5C%2FOP83%5C%2Fzb%5C%2FMv81%5C%2FzX%5C%2FR%5C%2F9L%5C%2F0v%5C%2FO%5C%2F9I%5C%2F0T%5C%2FS%5C%2F9A%5C%2Fzn%5C%2FPf89%5C%2Fzn%5C%2FNf9K%5C%2F07%5C%2FN%5C%2F83%5C%2Fzn%5C%2FNv83%5C%2Fzv%5C%2FO%5C%2F9A%5C%2F0H%5C%2FOf8%5C%2F%5C%2Fzj%5C%2FPP83%5C%2Fzj%5C%2FS%5C%2F87%5C%2Fzj%5C%2FNv84%5C%2Fzf%5C%2FOf83%5C%2Fzf%5C%2FOf83%5C%2Fzb%5C%2FNv9L%5C%2Fzj%5C%2FNv82%5C%2Fzb%5C%2FN%5C%2F85%5C%2Fzf%5C%2FN%5C%2F9J%5C%2Fzf%5C%2FNv83%5C%2Fzj%5C%2FNv84%5C%2F0r%5C%2FSv83%5C%2Fzf%5C%2FMP%5C%2F%5C%2F%5C%2Fzb%5C%2FMv82%5C%2Fzb%5C%2FOf85%5C%2Fz7%5C%2FNv8%5C%2F%5C%2F0r%5C%2FS%5C%2F85%5C%2F0H%5C%2FQP9B%5C%2F0D%5C%2FNf89%5C%2Fzj%5C%2FOv83%5C%2Fzv%5C%2FNv8%5C%2F%5C%2F0f%5C%2FSv9O%5C%2F0ZeXv%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F1X%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F9B%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2FTP%5C%2F%5C%2F%5C%2F1b%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F0%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F9N%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2F%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%5C%2Fv7%2B%22%2C%22date%22%3A%222021-08-07%22%2C%22data%22%3A%5B%7B%22start%22%3A0%2C%22stop%22%3A1439%2C%22value%22%3A%22UA8AUBQAUAwAUBoAUAEAYCcAUBkAUB4AUBgAUCAAUAEAUBkAUAwAYAsAYB8AYB0AYBgAYCoAYBgAYB4AUCcAUBsAUB8AUBwAUBIAYBkAYB8AUBoAUBMAUCEAUCIAYBYAUBwAUCAAUBgAUCAAUBcAYBsAYCUAATIPYD0KECQAYDMAYB0AYAsAYCAAYDwAYCIAYB0AYBcAYCQAYB0AYBAAYCMAYAoAYCIAYCEAYCYAYBsAYBUAYAYAYCIAYCMAUB0AUCAAUBYAUCoAUBEAUC8AUB0AUBYAUDMAUDoAUBkAUC0AUBQAUBwAUA0AUBsAUAoAUCEAUBYAUAwAUB4AUAwAUCcAUCYAUCwKYDUAAUUlEC8IYEMAYEgAYDoAYBAAUAMAUBkAWgAAWgAAWgAAWgAAWgAAUAgAWgAAUBAAUAQAUA4AUA8AUAkAUAIAUAYAUAcAUAIAWgAAUAQAUAkAUAEAUBkAUCUAWgAAUAYAUBEAWgAAUBYAWgAAUAYAWgAAWgAAWgAAWgAAUBcAUAcAWgAAUBUAUAoAUAIAWgAAUAQAUAYAUCgAWgAAUAgAWgAAWgAAUAwAWwAAXCMAUBQAWwAAUAIAWgAAWgAAWgAAWgAAWgAAWgAAWgAAWgAAWREAWQIAUAMAWSEAUDoAUDIAUB8AUCEAUC4AXB4AUA4AWgAAUBIAUA8AUBAAUCUAUCIAUAMAUAEAUAsAUAMAUCwAUBYAWgAAWgAAWgAAWgAAWgAAWgAAUAYAWgAAWgAAWgAAUAYAWwAAWgAAUAYAXAQAUAMAUBsAUBcAUCAAWwAAWgAAWgAAWgAAWgAAUBgAUB4AWgAAUAcAUAwAWQIAWQkAUAEAUAIAWgAAUAoAWgAAUAYAUB0AWgAAWgAAUAkAWgAAWSwAUBIAWgAAUC4AWSYAWgAAUAYAUAoAUAkAUAIAUAcAWgAAUAEAUBEAUBgAUBcAWRYAUA0AWSgAUB4AUDQAUBoAXA4AUA8AUBwAUA8AUA4AUA4AWgAAUAIAUCMAWgAAUCwAUBgAUAYAUAAAUAAAUAAAUAAAUAAAUAAAUAAAUAAAUAAAWwAAUAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAeSEAeQ8AcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcBcAcAAAcAAAcCYOcBUAUAAAUAAAUAAAUAAAUAUAUAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcCgAeQAAcAAAcAAAcAAAcAAAcAAAcAYAcAAAcBgAeQAAcAAAcAAAegAAegAAcAAAcAcAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcCkAeQAAcAcAcAAAcAAAcAwAcAAAcAAAcAIAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcCIAeQAAcAAAcAAAcAAAcAAAcAAAeRwAeQAAWgAAUAAAUAAAUAAAUAAAUAAAcAAAcAAAcBoAeScAeQAAegAAcBkAeQAAUAAAUAAAUAAAUAAAUAAAUAAAcAAAcAAAcAAAcAAAcAAAcAAAegAAegAAcAAAcAAAcBgAeQAAcAAAcAAAcAAAcAAAcAAAcAkAegAAegAAcAcAcAAAcAcAcAAAcAAAcAAAcAAAcA8AeQAAcAAAcAAAeRQAcAwAUAAAUAAAUAAAUAAAUAAAUAAAcAAAcBEAcA0AcAAAWQsAUAAAUAAAUAAAUAAAUAAAcAAAcAoAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAYAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcBYAegAAcAAAcAAAegAAcAcAcAAAcAAAcAAAcAAAcAAAeRkAegAAegAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAEAcAAAcAAAcAAAcAUAcAQAcAAAcBIAeQAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcBsAcAAAcAAAcBcAeQAAUAAAUAAAUAAAUAAAUAAAUBQAcBYAUAAAUAAAUAoAWRYAWTQAWQAAUAAAUAAAUAAAcAAAcAAAcAAAcAAAcAAAcAMAcAAAcAQAcAAAcAAAcAAAcDMAeSIAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcAAAcBQAeQwAcAAAcAAAcAAAcAMAcAAAeSoAcA8AcDMAcAYAeQoAcAwAcFQAcEMAeVIAaTYAbBcNYAsAYBIAYAIAYAIAYBUAYCwAYBMAYDYAYCkAYDcAUCoAUCcAUAUAUBAAWgAAYBoAYBcAYCgAUAMAUAYAUBYAUA4AUBgAUAgAUAgAUAsAUAsAUA4AUAMAUAYAUAQAUBIAASsSUDAAUDAAUBAAYAYAUBAAUAUAUCAAUBoAUCAAUBAAUAoAYAIAUAQAUAgAUCcAUAsAUCIAUCUAUAoAUA4AUB8AUBkAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAAfgAA%22%2C%22tz%22%3A32%2C%22did%22%3A%22DA932FFFFE8816E7%22%2C%22src%22%3A24%7D%5D%2C%22summary%22%3A%22%7B%5C%22v%5C%22%3A6%2C%5C%22slp%5C%22%3A%7B%5C%22st%5C%22%3A1628296479%2C%5C%22ed%5C%22%3A1628296479%2C%5C%22dp%5C%22%3A0%2C%5C%22lt%5C%22%3A0%2C%5C%22wk%5C%22%3A0%2C%5C%22usrSt%5C%22%3A-1440%2C%5C%22usrEd%5C%22%3A-1440%2C%5C%22wc%5C%22%3A0%2C%5C%22is%5C%22%3A0%2C%5C%22lb%5C%22%3A0%2C%5C%22to%5C%22%3A0%2C%5C%22dt%5C%22%3A0%2C%5C%22rhr%5C%22%3A0%2C%5C%22ss%5C%22%3A0%7D%2C%5C%22stp%5C%22%3A%7B%5C%22ttl%5C%22%3A18272%2C%5C%22dis%5C%22%3A10627%2C%5C%22cal%5C%22%3A510%2C%5C%22wk%5C%22%3A41%2C%5C%22rn%5C%22%3A50%2C%5C%22runDist%5C%22%3A7654%2C%5C%22runCal%5C%22%3A397%2C%5C%22stage%5C%22%3A%5B%7B%5C%22start%5C%22%3A327%2C%5C%22stop%5C%22%3A341%2C%5C%22mode%5C%22%3A1%2C%5C%22dis%5C%22%3A481%2C%5C%22cal%5C%22%3A13%2C%5C%22step%5C%22%3A680%7D%2C%7B%5C%22start%5C%22%3A342%2C%5C%22stop%5C%22%3A367%2C%5C%22mode%5C%22%3A3%2C%5C%22dis%5C%22%3A2295%2C%5C%22cal%5C%22%3A95%2C%5C%22step%5C%22%3A2874%7D%2C%7B%5C%22start%5C%22%3A368%2C%5C%22stop%5C%22%3A377%2C%5C%22mode%5C%22%3A4%2C%5C%22dis%5C%22%3A1592%2C%5C%22cal%5C%22%3A88%2C%5C%22step%5C%22%3A1664%7D%2C%7B%5C%22start%5C%22%3A378%2C%5C%22stop%5C%22%3A386%2C%5C%22mode%5C%22%3A3%2C%5C%22dis%5C%22%3A1072%2C%5C%22cal%5C%22%3A51%2C%5C%22step%5C%22%3A1245%7D%2C%7B%5C%22start%5C%22%3A387%2C%5C%22stop%5C%22%3A393%2C%5C%22mode%5C%22%3A4%2C%5C%22dis%5C%22%3A1036%2C%5C%22cal%5C%22%3A57%2C%5C%22step%5C%22%3A1124%7D%2C%7B%5C%22start%5C%22%3A394%2C%5C%22stop%5C%22%3A398%2C%5C%22mode%5C%22%3A3%2C%5C%22dis%5C%22%3A488%2C%5C%22cal%5C%22%3A19%2C%5C%22step%5C%22%3A607%7D%2C%7B%5C%22start%5C%22%3A399%2C%5C%22stop%5C%22%3A414%2C%5C%22mode%5C%22%3A4%2C%5C%22dis%5C%22%3A2220%2C%5C%22cal%5C%22%3A120%2C%5C%22step%5C%22%3A2371%7D%2C%7B%5C%22start%5C%22%3A415%2C%5C%22stop%5C%22%3A427%2C%5C%22mode%5C%22%3A3%2C%5C%22dis%5C%22%3A1268%2C%5C%22cal%5C%22%3A59%2C%5C%22step%5C%22%3A1489%7D%2C%7B%5C%22start%5C%22%3A428%2C%5C%22stop%5C%22%3A433%2C%5C%22mode%5C%22%3A1%2C%5C%22dis%5C%22%3A152%2C%5C%22cal%5C%22%3A4%2C%5C%22step%5C%22%3A238%7D%2C%7B%5C%22start%5C%22%3A434%2C%5C%22stop%5C%22%3A444%2C%5C%22mode%5C%22%3A3%2C%5C%22dis%5C%22%3A2295%2C%5C%22cal%5C%22%3A95%2C%5C%22step%5C%22%3A2874%7D%2C%7B%5C%22start%5C%22%3A445%2C%5C%22stop%5C%22%3A455%2C%5C%22mode%5C%22%3A4%2C%5C%22dis%5C%22%3A1592%2C%5C%22cal%5C%22%3A88%2C%5C%22step%5C%22%3A1664%7D%2C%7B%5C%22start%5C%22%3A456%2C%5C%22stop%5C%22%3A466%2C%5C%22mode%5C%22%3A3%2C%5C%22dis%5C%22%3A1072%2C%5C%22cal%5C%22%3A51%2C%5C%22step%5C%22%3A1245%7D%2C%7B%5C%22start%5C%22%3A467%2C%5C%22stop%5C%22%3A477%2C%5C%22mode%5C%22%3A4%2C%5C%22dis%5C%22%3A1036%2C%5C%22cal%5C%22%3A57%2C%5C%22step%5C%22%3A1124%7D%2C%7B%5C%22start%5C%22%3A478%2C%5C%22stop%5C%22%3A488%2C%5C%22mode%5C%22%3A3%2C%5C%22dis%5C%22%3A488%2C%5C%22cal%5C%22%3A19%2C%5C%22step%5C%22%3A607%7D%2C%7B%5C%22start%5C%22%3A489%2C%5C%22stop%5C%22%3A499%2C%5C%22mode%5C%22%3A4%2C%5C%22dis%5C%22%3A2220%2C%5C%22cal%5C%22%3A120%2C%5C%22step%5C%22%3A2371%7D%2C%7B%5C%22start%5C%22%3A500%2C%5C%22stop%5C%22%3A511%2C%5C%22mode%5C%22%3A3%2C%5C%22dis%5C%22%3A1268%2C%5C%22cal%5C%22%3A59%2C%5C%22step%5C%22%3A1489%7D%2C%7B%5C%22start%5C%22%3A512%2C%5C%22stop%5C%22%3A522%2C%5C%22mode%5C%22%3A1%2C%5C%22dis%5C%22%3A152%2C%5C%22cal%5C%22%3A4%2C%5C%22step%5C%22%3A238%7D%5D%7D%2C%5C%22goal%5C%22%3A8000%2C%5C%22tz%5C%22%3A%5C%2228800%5C%22%7D%22%2C%22source%22%3A24%2C%22type%22%3A0%7D%5D";

            Regex findDate = new(@".*?date%22%3A%22(.*?)%22%2C%22data.*?");
            Regex findStep = new(@".*?ttl%5C%22%3A(.*?)%2C%5C%22dis.*?");
            Match dateMatch = findDate.Match(data_json);
            if (dateMatch.Success)
                data_json = Regex.Replace(data_json, dateMatch.Groups[1].Value, today);
            Match stepMatch = findStep.Match(data_json);
            if (stepMatch.Success)
                data_json = Regex.Replace(data_json, stepMatch.Groups[1].Value, step);

            var app_token = await Motion_GetAppToken(login_token, fakeIP);
            var t = GetTimeStamp_Milliseconds();

            var url = $"https://api-mifit-cn.huami.com/v1/data/band_data.json?&t={t}";
            Dictionary<string, string> headers = new()
            {
                { "apptoken", app_token },
                { "Content-Type", "application/x-www-form-urlencoded" },
                { "X-Forwarded-For", fakeIP },
            };
            var data = $"userid={userid}&last_sync_data_time=1597306380&device_type=0&last_deviceid=DA932FFFFE8816E7&data_json={data_json}";
            var client1 = new RestClient(url);
            RestRequest request1 = new() { Method = Method.Post };
            request1.AddOrUpdateHeaders(headers);
            request1.AddParameter("text/plain", data, ParameterType.RequestBody);
            RestResponse response1 = await client1.ExecuteAsync(request1);
            var jObject = JsonSerializer.Deserialize<JsonObject>(response1.Content);

            return (true, step, $"修改步数({step}) [{jObject["message"]?.ToString()}]");
        }

        static string GetAccessToken(string location)
        {
            var lineList = Regex.Matches(location, @"(?<=access=).*?(?=&)").OfType<Match>().Select(m => m.Groups[0].Value).ToList();
            return lineList.FirstOrDefault();
        }

        static long GetTimeStamp_Milliseconds()
        {
            DateTime currentTime = DateTime.UtcNow;
            DateTime unixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            TimeSpan elapsedTime = currentTime - unixEpoch;
            return (long)elapsedTime.TotalMilliseconds;
        }

        static string GetFakeIP()
        {
            Random rd = new(Guid.NewGuid().GetHashCode());
            return $"233.{rd.Next(64, 117)}.{rd.Next(0, 255)}.{rd.Next(0, 255)}";
        }

        static string DesensitizeUserName(string user)
        {
            if (string.IsNullOrWhiteSpace(user))
                return "";

            if (user.Length <= 8)
            {
                int ln = Math.Max((int)Math.Floor((double)user.Length / 3), 1);
                return user[..ln] + "**" + user[^ln..];
            }

            return user[..3] + "**" + user[^4..];
        }

        static DateTime GetBeiJingTime()
        {
            DateTime nowUtc = DateTime.UtcNow;
            TimeZoneInfo beijingTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
            DateTime nowBeiJing = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, beijingTimeZone);
            return nowBeiJing;
        }
    }

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

            Dictionary<string, object> dic = new()
            {
                { "title", title },
                { "body", content },
                { "device_key", bark_Devicekey }
            };

            var res = await Util.HttpPostBody(url, headers, dic);
            var jObject = JsonSerializer.Deserialize<JsonObject>(res);
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
        public static async Task<string> HttpPostBody(string url, Dictionary<string, string> headers, Dictionary<string, object> dic)
        {
            try
            {
                HttpClient _client = new();

                var p = JsonSerializer.Serialize(dic);

                HttpContent httpContent = new StringContent(p);
                httpContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                foreach (var item in headers)
                    if (httpContent.Headers.Contains(item.Key) == false)
                        httpContent.Headers.Add(item.Key, item.Value);

                HttpResponseMessage response = await _client.PostAsync(url, httpContent);

                string result = string.Empty;
                if (response.IsSuccessStatusCode)
                    result = await response.Content.ReadAsStringAsync();

                return result;
            }
            catch (Exception ex)
            {
                return ex?.Message ?? "error";
            }
        }

        public static T Deserialize<T>(string json)
        {
            return string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            });
        }

        public static string GetEnvValue(string key) => Environment.GetEnvironmentVariable(key);
    }

    public class Conf
    {
        public string Bark_Devicekey { get; set; }
        public string Bark_Icon { get; set; }
        public string Smtp_Server { get; set; }
        public int Smtp_Port { get; set; }
        public string Smtp_Email { get; set; }
        public string Smtp_Password { get; set; }
        public List<string> Receive_Email_List { get; set; }
        public bool UseConcurrent { get; set; }
        public int Sleep_Gap_Second { get; set; }
        public List<People> Peoples { get; set; }
    }

    public class People
    {
        public string User { get; set; }
        public string Pwd { get; set; }
        public int MinStep { get; set; }
        public int MaxStep { get; set; }
    }
    public class QueueModel
    {
        public string User { get; set; }
        public string Pwd { get; set; }
        public int MinStep { get; set; }
        public int MaxStep { get; set; }
        public bool IsPhone { get; set; }
        public string FakeIP { get; set; }
        public DateTime NowBeiJing { get; set; }
    }
    public class QueueResult
    {
        public string User { get; set; }
        public bool Success { get; set; }
        public string Step { get; set; }
        public string Msg { get; set; }
    }
}
