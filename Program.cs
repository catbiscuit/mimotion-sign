using System.Collections.Concurrent;

namespace MiMotionSign
{
    internal class Program
    {
        private static readonly ConcurrentQueue<QueueModel> taskQueue = new();
        private static readonly ConcurrentBag<QueueResult> results = [];
        private static int consumerCount = 4;
        private static int maxTaskDurationMilliseconds = 1000 * 60;

        static async Task Main(string[] args)
        {
            Console.WriteLine(Util.GetBeiJingTimeStr() + " - Start running...");

            await Run();

            Console.WriteLine(Util.GetBeiJingTimeStr() + " - End running...");

#if DEBUG
            Console.WriteLine("Program Run On Debug");
            Console.ReadLine();
#else
            Console.WriteLine("Program Run On Release");
#endif
        }

        static async Task Run()
        {
            Conf conf = Util.GetEnvValue("CONF")?.TryToObject<Conf>();
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

            if (conf.ConsumerCount > 0)
                consumerCount = conf.ConsumerCount;
            if (conf.MaxTaskDurationSeconds > 0)
                maxTaskDurationMilliseconds = conf.MaxTaskDurationSeconds * 1000;

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
                string fakeIP = Util.GetFakeIP();

                int min = people.MinStep;
                int max = people.MaxStep;
                if (min <= 0)
                    min = 18000;
                if (max <= 0)
                    max = 25000;

                DateTime nowBeiJing = Util.GetBeiJingTime();

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

            List<string> message_all = ["当前运行模式：【" + (conf.UseConcurrent ? $"并行，{consumerCount} 个消费者" : $"顺序执行，间隔 {sleepGapSecond} 秒左右") + "】"];

            for (int i = 0; i < queueModels.Count; i++)
            {
                var people = queueModels[i];

                string current = i + 1 + "、" + Util.DesensitizeStr(people.User);
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

#if DEBUG
            Console.WriteLine(content);
#endif

            Console.WriteLine("Send");
            SendUtil.SendEMail(conf.Smtp_Server, conf.Smtp_Port, conf.Smtp_Email, conf.Smtp_Password, conf.Receive_Email_List, title, content, topicName);
            await SendUtil.SendBark(conf.Bark_Devicekey, conf.Bark_Icon, title, content);
        }

        static async Task<List<QueueResult>> Sequence_Run(List<QueueModel> queueModels, int sleepGapSecond)
        {
            List<QueueResult> resultList = [];
            for (int i = 0; i < queueModels.Count; i++)
            {
                var currentResult = await Run_Single(queueModels[i], CancellationToken.None);
                resultList.Add(currentResult);

                if (i < (queueModels.Count - 1))
                {
                    Random rd = new(Guid.NewGuid().GetHashCode());
                    await Task.Delay(rd.Next(sleepGapSecond * 1000 - 2143, sleepGapSecond * 1000 + 2143));
                }
            }
            return resultList;
        }

        static async Task<List<QueueResult>> Concurrent_Run(List<QueueModel> queueModels)
        {
            // 添加任务到队列
            foreach (var item in queueModels)
                taskQueue.Enqueue(item);

            // 启动消费者
            var consumerTasks = new Task[consumerCount];
            for (int i = 0; i < consumerCount; i++)
                consumerTasks[i] = ConsumeTasksAsync();

            // 等待所有消费者完成
            await Task.WhenAll(consumerTasks);

            List<QueueResult> resultList = [];
            foreach (var item in results)
            {
                resultList.Add(new QueueResult()
                {
                    User = item.User,
                    Success = item.Success,
                    Step = item.Step,
                    Msg = item.Msg,
                });
            }

            return resultList;
        }

        private static async Task ConsumeTasksAsync()
        {
            while (true)
            {
                if (taskQueue.TryDequeue(out QueueModel taskData))
                {
                    var cts = new CancellationTokenSource(maxTaskDurationMilliseconds);

                    var consumerResult = new QueueResult()
                    {
                        User = taskData.User,
                        Success = taskData.Success,
                        Step = taskData.Step,
                        Msg = taskData.Msg,
                    };

                    try
                    {
                        var task = ProcessTaskAsync(taskData, cts.Token);
                        await task;

                        consumerResult.Success = taskData.Success;
                        consumerResult.Step = taskData.Step;
                        consumerResult.Msg = taskData.Msg;
                        results.Add(consumerResult);
                    }
                    catch (OperationCanceledException)
                    {
                        consumerResult.Success = false;
                        consumerResult.Msg = "失败，引发了超时异常";
                        results.Add(consumerResult);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(Util.GetExceptionMsg(ex, ""));
                        consumerResult.Success = false;
                        consumerResult.Msg = "失败，" + (ex?.Message ?? "");
                        results.Add(consumerResult);
                    }
                    finally
                    {
                        cts?.Dispose();
                    }
                }
                else
                {
                    break;
                }
            }
        }

        private static async Task ProcessTaskAsync(QueueModel queueModel, CancellationToken cancellationToken)
        {
            var result = await Run_Single(queueModel, cancellationToken);
            queueModel.Success = result.Success;
            queueModel.Step = result.Step;
            queueModel.Msg = result.Msg;
        }

        static async Task<QueueResult> Run_Single(QueueModel queueModel, CancellationToken cancellationToken)
        {
            try
            {
                MotionClient motionClient = new(queueModel.User, queueModel.Pwd, queueModel.IsPhone, queueModel.FakeIP, queueModel.MinStep, queueModel.MaxStep, queueModel.NowBeiJing);
                (bool success, string step, string msg) = await motionClient.BrushStep(cancellationToken);
                return new QueueResult() { User = queueModel.User, Success = success, Step = step, Msg = msg, };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine(Util.GetExceptionMsg(ex, ""));
                return new QueueResult() { User = queueModel.User, Success = false, Step = "", Msg = $"单个执行错误:{(ex?.Message ?? "")}", };
            }
        }
    }

    public class Conf : SendConf
    {
        public bool UseConcurrent { get; set; }
        public int Sleep_Gap_Second { get; set; }
        public int MaxTaskDurationSeconds { get; set; }
        public int ConsumerCount { get; set; }
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
        public bool Success { get; set; }
        public string Step { get; set; }
        public string Msg { get; set; }
    }
    public class QueueResult
    {
        public string User { get; set; }
        public bool Success { get; set; }
        public string Step { get; set; }
        public string Msg { get; set; }
    }
}
