﻿using log4net;
using log4net.Appender;
using System;
using System.Reflection;
using log4net.Core;
using log4net.Config;
using System.Threading;
using FluentScheduler;
using System.Linq;
using System.Threading.Tasks;

namespace Log4ALA
{
    public class Log4ALAAppender : AppenderSkeleton
    {
        private static ILog log;
        private static ILog extraLog;
        public static bool isJobManagerInitialized = false;


        private LoggingEventSerializer serializer;

        private HTTPDataCollectorAPI.Collector httpDataCollectorAPI;

        public string WorkspaceId { get; set; }
        public string SharedKey { get; set; }
        public string LogType { get; set; }
        public string AzureApiVersion { get; set; }
        public string WebProxyHost { get; set; }
        public int? WebProxyPort { get; set; }
        public RuntimeContext RuntimeContext { get; set; }

        private static bool logMessageToFile = false;
        public bool LogMessageToFile { get; set; }

        public string ErrLoggerName { get; set; }


        static Log4ALAAppender()
        {
        }


        public override void ActivateOptions()
        {

            try
            {
                if (RuntimeContext.Equals(RuntimeContext.WEB_APP))
                {
                    InitJobManager();
                }

                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Log4ALA.internalLog4net.config"))
                {
                    XmlConfigurator.Configure(stream);
                }

                log = LogManager.GetLogger("Log4ALAInternalLogger");

                if (!string.IsNullOrWhiteSpace(ErrLoggerName))
                {
                    extraLog = LogManager.GetLogger(ErrLoggerName);
                }


                logMessageToFile = LogMessageToFile;

                if (string.IsNullOrWhiteSpace(WorkspaceId))
                {
                    throw new Exception($"the Log4ALAAppender property workspaceId [{WorkspaceId}] shouldn't be empty");
                }

                if (string.IsNullOrWhiteSpace(SharedKey))
                {
                    throw new Exception($"the Log4ALAAppender property sharedKey [{SharedKey}] shouldn't be empty");
                }

                if (string.IsNullOrWhiteSpace(LogType))
                {
                    throw new Exception($"the Log4ALAAppender property logType [{LogType}] shouldn't be empty");
                }

                if (string.IsNullOrWhiteSpace(AzureApiVersion))
                {
                    AzureApiVersion = "2016-04-01";
                }

                httpDataCollectorAPI = new HTTPDataCollectorAPI.Collector(WorkspaceId, SharedKey);

                serializer = new LoggingEventSerializer();

            }
            catch (Exception ex)
            {
                Error($"Unable to activate Log4ALAAppender: {ex.Message}", RuntimeContext);
            }
        }

        protected override void Append(LoggingEvent loggingEvent)
        {
            try
            {
                if (httpDataCollectorAPI != null)
                {
                    var content = serializer.SerializeLoggingEvents(new[] { loggingEvent }, RuntimeContext);
                    Info(content, RuntimeContext);

                    if (RuntimeContext.Equals(RuntimeContext.CONSOLE_APP))
                    {
                        Task.Run(() => httpDataCollectorAPI.Collect(LogType, content, AzureApiVersion, "DateValue")).ContinueWith(t =>
                        {
                            var exception = t.Exception.InnerException;
                            if(exception != null)
                            {
                                Error($"HTTPDataCollectorAPI job exception [{exception.Message}]", RuntimeContext.CONSOLE_APP, async: false);
                            }
                        },TaskContinuationOptions.OnlyOnFaulted);

                    }
                    else
                    {
                        //How to run Background Tasks in ASP.NET
                        //http://www.hanselman.com/blog/HowToRunBackgroundTasksInASPNET.aspx
                        JobManager.AddJob(() =>
                        {
                            httpDataCollectorAPI.Collect(LogType, content, AzureApiVersion, "DateValue");
                        }, (s) => { s.WithName($"{Guid.NewGuid().ToString()}_BlobTextApp"); s.ToRunNow(); });

                    }
                }
            }
            catch (Exception ex)
            {
                Error($"Unable to send data to Azure Log Analytics: {ex.Message}", RuntimeContext);
            }
        }


        public static void Error(string logMessage, RuntimeContext context = RuntimeContext.CONSOLE_APP, bool async = true)
        {
            if (log != null)
            {
                if (async)
                {
                    //http://www.ben-morris.com/using-asynchronous-log4net-appenders-for-high-performance-logging/
                    ThreadPool.QueueUserWorkItem(task => log.Error(logMessage));
                    if (extraLog != null)
                    {
                        if (context.Equals(RuntimeContext.CONSOLE_APP))
                        {
                            //http://www.ben-morris.com/using-asynchronous-log4net-appenders-for-high-performance-logging/
                            ThreadPool.QueueUserWorkItem(task => extraLog.Error(logMessage));
                        }
                        else
                        {
                            //How to run Background Tasks in ASP.NET
                            //http://www.hanselman.com/blog/HowToRunBackgroundTasksInASPNET.aspx
                            JobManager.AddJob(() =>
                            {
                                extraLog.Error(logMessage);
                            }, (s) => { s.WithName($"{Guid.NewGuid().ToString()}_LogError"); s.ToRunNow(); });
                        }
                    }
                }
                else
                {
                    log.Error(logMessage);
                    if (extraLog != null)
                    {
                        extraLog.Error(logMessage);
                    }

                }
            }
        }

        public static void Info(string logMessage, RuntimeContext context = RuntimeContext.CONSOLE_APP)
        {
            if (logMessageToFile && log != null)
            {
                if (context.Equals(RuntimeContext.CONSOLE_APP))
                {
                    //http://www.ben-morris.com/using-asynchronous-log4net-appenders-for-high-performance-logging/
                    ThreadPool.QueueUserWorkItem(task => log.Info(logMessage));
                }
                else
                {
                    //How to run Background Tasks in ASP.NET
                    //http://www.hanselman.com/blog/HowToRunBackgroundTasksInASPNET.aspx
                    JobManager.AddJob(() =>
                    {
                        log.Info(logMessage);
                    }, (s) => { s.WithName($"{Guid.NewGuid().ToString()}_LogInfo"); s.ToRunNow(); });
                }
            }
        }

        public static void InitJobManager(RuntimeContext context = RuntimeContext.CONSOLE_APP)
        {
            if (!isJobManagerInitialized)
            {
                isJobManagerInitialized = true;
                JobManager.JobEnd += JobEndEvent;
                JobManager.JobException += JobExceptionEvent;
            }
        }

        private static void JobExceptionEvent(JobExceptionInfo obj)
        {
            if (obj.Exception != null)
            {
                Error($"JobManager JobException of job [{obj.Name}]  - [{obj.Exception.StackTrace}]", RuntimeContext.CONSOLE_APP, async: false);
                string jobName = obj.Name;
                RemoveJobManagerJob(jobName);
            }
        }

        public static void JobEndEvent(JobEndInfo job)
        {
            string jobName = job.Name;
            RemoveJobManagerJob(jobName);
        }

        private static void RemoveJobManagerJob(string jobName)
        {
            try
            {
                JobManager.RemoveJob(jobName);
            }
            catch (Exception)
            {
                Error($"JobManager job [{jobName}] couldn't removed", RuntimeContext.CONSOLE_APP, async: false);
            }
        }


    }

    public enum RuntimeContext
    {
        //Default
        CONSOLE_APP = 0,

        WEB_APP = 1
    }



}
