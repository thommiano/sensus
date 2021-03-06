// Copyright 2014 The Rector & Visitors of the University of Virginia
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using Newtonsoft.Json;
using SensusService.Probes.Location;
using SensusUI.UiProperties;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Xamarin;
using Xamarin.Geolocation;
using System.Security.Cryptography;
using System.Text;
using SensusService.Probes;

namespace SensusService
{
    /// <summary>
    /// Provides platform-independent service functionality.
    /// </summary>
    public abstract class SensusServiceHelper : IDisposable
    {
        #region static members
        public const string SENSUS_CALLBACK_KEY = "SENSUS-CALLBACK";
        public const string SENSUS_CALLBACK_ID_KEY = "SENSUS-CALLBACK-ID";
        public const string SENSUS_CALLBACK_REPEATING_KEY = "SENSUS-CALLBACK-REPEATING";

        private static SensusServiceHelper _singleton;
        private static object _staticLockObject = new object();
        protected static readonly string XAMARIN_INSIGHTS_APP_KEY = "97af5c4ab05c6a69d2945fd403ff45535f8bb9bb";
        private static readonly string ENCRYPTION_KEY = "Making stuff private!";
        private static readonly string _shareDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "share");
        private static readonly string _logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "sensus_log.txt");
        private static readonly string _serializationPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "sensus_service_helper.json");
        private static readonly JsonSerializerSettings _serializationSettings = new JsonSerializerSettings
            {
                PreserveReferencesHandling = PreserveReferencesHandling.Objects,
                ReferenceLoopHandling = ReferenceLoopHandling.Serialize,
                TypeNameHandling = TypeNameHandling.All,
                ConstructorHandling = ConstructorHandling.AllowNonPublicDefaultConstructor,
            };

        private static byte[] EncryptionKeyBytes
        {
            get
            {
                byte[] encryptionKeyBytes = new byte[32];
                byte[] bytes = Encoding.Default.GetBytes(ENCRYPTION_KEY);
                Array.Copy(bytes, encryptionKeyBytes, Math.Min(bytes.Length, encryptionKeyBytes.Length));
                return encryptionKeyBytes;
            }
        }

        public static SensusServiceHelper Get()
        {
            // service helper be null for a brief period between the time when the app starts and when the service constructs the helper object.
            int triesLeft = 10;
            while (triesLeft-- > 0)
            {
                lock (_staticLockObject)
                    if (_singleton == null)
                    {
                        Console.Error.WriteLine("Waiting for service to construct helper object.");
                        Thread.Sleep(1000);
                    }
                    else
                        break;
            }

            lock (_staticLockObject)
                if (_singleton == null)
                {
                    string error = "Failed to get service helper.";

                    // don't try to access/write the logger or raise a sensus-based exception, since these will call back into the current method
                    Console.Error.WriteLine(error);

                    Exception ex = new Exception(error);

                    try { Insights.Report(ex, ReportSeverity.Error); }
                    catch (Exception ex2) { Console.Error.WriteLine("Failed to report exception to Xamarin Insights:  " + ex2.Message); }

                    throw ex;
                }

            return _singleton;
        }

        public static SensusServiceHelper Load<T>() where T : SensusServiceHelper, new()
        {
            SensusServiceHelper sensusServiceHelper = null;

            try
            {
                sensusServiceHelper = JsonConvert.DeserializeObject<T>(AesDecrypt(File.ReadAllBytes(_serializationPath)), _serializationSettings);
                sensusServiceHelper.Logger.Log("Deserialized service helper with " + sensusServiceHelper.RegisteredProtocols.Count + " protocols.", LoggingLevel.Normal, typeof(T));  
            }
            catch (Exception ex)
            {
                Console.Out.WriteLine("Failed to deserialize Sensus service helper:  " + ex.Message);
                Console.Out.WriteLine("Creating new Sensus service helper.");

                try
                {
                    sensusServiceHelper = new T();
                    sensusServiceHelper.Save();
                }
                catch (Exception ex2)
                {
                    Console.Out.WriteLine("Failed to create/save new Sensus service helper:  " + ex2.Message);
                }
            }

            return sensusServiceHelper;
        }

        #region encryption
        public static byte[] AesEncrypt(string s)
        {
            using (AesCryptoServiceProvider aes = new AesCryptoServiceProvider())
            {
                byte[] encryptionKeyBytes = EncryptionKeyBytes;
                aes.KeySize = encryptionKeyBytes.Length * 8;

                byte[] initialization = new byte[16];
                aes.BlockSize = initialization.Length * 8;

                using (ICryptoTransform transform = aes.CreateEncryptor(encryptionKeyBytes, initialization))
                {
                    byte[] unencrypted = Encoding.Unicode.GetBytes(s);
                    return transform.TransformFinalBlock(unencrypted, 0, unencrypted.Length);
                }
            }
        }

        public static string AesDecrypt(byte[] bytes)
        {
            using (AesCryptoServiceProvider aes = new AesCryptoServiceProvider())
            {
                byte[] encryptionKeyBytes = EncryptionKeyBytes;
                aes.KeySize = encryptionKeyBytes.Length * 8;

                byte[] initialization = new byte[16];
                aes.BlockSize = initialization.Length * 8;

                using (ICryptoTransform transform = aes.CreateDecryptor(encryptionKeyBytes, initialization))
                {
                    return Encoding.Unicode.GetString(transform.TransformFinalBlock(bytes, 0, bytes.Length));
                }
            }
        }
        #endregion
        #endregion

        /// <summary>
        /// Raised when the service helper has stopped.
        /// </summary>
        public event EventHandler Stopped;       

        private bool _stopped;
        private Logger _logger;
        private List<Protocol> _registeredProtocols;
        private List<string> _runningProtocolIds;
        private string _healthTestCallbackId;
        private int _healthTestDelayMS;
        private int _healthTestCount;
        private int _healthTestsPerProtocolReport;
        private Dictionary<string, Tuple<Action<CancellationToken>, CancellationTokenSource, string>> _idCallbackCancellerMessage;
        private MD5 _md5Hash;

        private readonly object _locker = new object();

        [JsonIgnore]
        public Logger Logger
        {
            get { return _logger; }
        }

        public List<Protocol> RegisteredProtocols
        {
            get { return _registeredProtocols; }
        }

        public List<string> RunningProtocolIds
        {
            get{ return _runningProtocolIds; }
        }                  

        [EntryIntegerUiProperty("Health Test Delay (MS):", true, 9)]
        public int HealthTestDelayMS
        {
            get { return _healthTestDelayMS; }
            set 
            {
                if (value <= 1000)
                    value = 1000;
                
                if (value != _healthTestDelayMS)
                {
                    _healthTestDelayMS = value;

                    if (_healthTestCallbackId != null)
                        _healthTestCallbackId = RescheduleRepeatingCallback(_healthTestCallbackId, _healthTestDelayMS, _healthTestDelayMS);

                    Save();
                }
            }
        }

        [EntryIntegerUiProperty("Health Tests Per Report:", true, 10)]
        public int HealthTestsPerProtocolReport
        {
            get { return _healthTestsPerProtocolReport; }
            set
            {
                if (value != _healthTestsPerProtocolReport)
                {
                    _healthTestsPerProtocolReport = value; 
                    Save();
                }
            }
        }

        [ListUiProperty("Logging Level:", true, 11, new object[] { LoggingLevel.Off, LoggingLevel.Normal, LoggingLevel.Verbose, LoggingLevel.Debug })]
        public LoggingLevel LoggingLevel
        {
            get { return _logger.Level; }
            set 
            {
                if (value != _logger.Level)
                {
                    _logger.Level = value; 
                    Save();
                }
            }
        } 

        #region platform-specific properties
        [JsonIgnore]
        public abstract bool IsCharging { get; }

        [JsonIgnore]
        public abstract bool WiFiConnected { get; }

        [JsonIgnore]
        public abstract string DeviceId { get; }       

        [JsonIgnore]
        public abstract string OperatingSystem { get; }

        protected abstract Geolocator Geolocator { get; }
        #endregion

        protected SensusServiceHelper()
        {
            lock (_staticLockObject)
                if (_singleton != null)
                    _singleton.Dispose();

            _stopped = true;
            _registeredProtocols = new List<Protocol>();
            _runningProtocolIds = new List<string>();
            _healthTestCallbackId = null;
            _healthTestDelayMS = 60000;
            _healthTestCount = 0;
            _healthTestsPerProtocolReport = 5;
            _idCallbackCancellerMessage = new Dictionary<string, Tuple<Action<CancellationToken>, CancellationTokenSource, string>>();
            _md5Hash = MD5.Create();

            if (!Directory.Exists(_shareDirectory))
                Directory.CreateDirectory(_shareDirectory); 

            #if DEBUG
            LoggingLevel loggingLevel = LoggingLevel.Debug;
            #else
            LoggingLevel loggingLevel = LoggingLevel.Normal;
            #endif

            _logger = new Logger(_logPath, loggingLevel, Console.Error);
            _logger.Log("Log file started at \"" + _logPath + "\".", LoggingLevel.Normal, GetType());

            GpsReceiver.Get().Initialize(Geolocator);  // initialize GPS receiver with platform-specific geolocator

            if (Insights.IsInitialized)
                _logger.Log("Xamarin Insights is already initialized.", LoggingLevel.Normal, GetType());
            else
            {
                try
                {
                    _logger.Log("Initializing Xamarin Insights.", LoggingLevel.Normal, GetType());
                    InitializeXamarinInsights();
                }
                catch (Exception ex)
                {
                    _logger.Log("Failed to initialize Xamarin insights:  " + ex.Message, LoggingLevel.Normal, GetType());
                }
            }

            lock (_staticLockObject)
                _singleton = this;
        }

        public string GetMd5Hash(string s)
        {
            if (s == null)
                return null;
            
            StringBuilder hashBuilder = new StringBuilder();
            foreach (byte b in _md5Hash.ComputeHash(Encoding.UTF8.GetBytes(s)))
                hashBuilder.Append(b.ToString("x"));

            return hashBuilder.ToString();
        }           

        #region platform-specific methods
        protected abstract void InitializeXamarinInsights();

        public abstract bool Use(Probe probe);

        protected abstract void ScheduleRepeatingCallback(string callbackId, int initialDelayMS, int repeatDelayMS, string userNotificationMessage);

        protected abstract void ScheduleOneTimeCallback(string callbackId, int delayMS, string userNotificationMessage);

        protected abstract void UnscheduleCallback(string callbackId, bool repeating);

        public abstract void PromptForAndReadTextFileAsync(string promptTitle, Action<string> callback);

        public abstract void ShareFileAsync(string path, string subject);

        public abstract void TextToSpeechAsync(string text, Action callback);

        public abstract void PromptForInputAsync(string prompt, bool startVoiceRecognizer, Action<string> callback);

        public abstract void IssueNotificationAsync(string message, string id);

        public abstract void FlashNotificationAsync(string message, Action callback);

        public abstract void KeepDeviceAwake();

        public abstract void LetDeviceSleep();

        public abstract void UpdateApplicationStatus(string status);
        #endregion

        #region add/remove running protocol ids
        public void AddRunningProtocolId(string id)
        {
            lock (_locker)
            {
                if (!_runningProtocolIds.Contains(id))
                {
                    _runningProtocolIds.Add(id);
                    Save();

                    SensusServiceHelper.Get().UpdateApplicationStatus(_runningProtocolIds.Count + " protocol" + (_runningProtocolIds.Count == 1 ? " is " : "s are") + " running");
                }

                if (_healthTestCallbackId == null)
                    _healthTestCallbackId = ScheduleRepeatingCallback(TestHealth, _healthTestDelayMS, _healthTestDelayMS);
            }
        }

        public void RemoveRunningProtocolId(string id)
        {
            lock (_locker)
            {
                if (_runningProtocolIds.Remove(id))
                {
                    Save();

                    SensusServiceHelper.Get().UpdateApplicationStatus(_runningProtocolIds.Count + " protocol" + (_runningProtocolIds.Count == 1 ? " is " : "s are") + " running");
                }

                if (_runningProtocolIds.Count == 0)
                {
                    UnscheduleRepeatingCallback(_healthTestCallbackId);
                    _healthTestCallbackId = null;
                }
            }
        }

        public bool ProtocolShouldBeRunning(Protocol protocol)
        {
            return _runningProtocolIds.Contains(protocol.Id);
        }
        #endregion

        public void Save()
        {
            lock (_locker)
            {
                try
                {
                    using (FileStream file = new FileStream(_serializationPath, FileMode.Create, FileAccess.Write))
                    {
                        byte[] encryptedBytes = AesEncrypt(JsonConvert.SerializeObject(this, _serializationSettings));
                        file.Write(encryptedBytes, 0, encryptedBytes.Length);
                        file.Close();
                    }
                }
                catch (Exception ex)
                {
                    _logger.Log("Failed to serialize Sensus service helper:  " + ex.Message, LoggingLevel.Normal, GetType());
                }
            }
        }

        public void StartAsync(Action callback)
        {
            new Thread(() =>
                {
                    Start();

                    if(callback != null)
                        callback();

                }).Start();
        }

        /// <summary>
        /// Starts platform-independent service functionality, including protocols that should be running. Okay to call multiple times, even if the service is already running.
        /// </summary>
        public void Start()
        {
            lock (_locker)
            {
                if (_stopped)
                    _stopped = false;
                else
                    return;

                foreach (Protocol protocol in _registeredProtocols)
                    if (!protocol.Running && _runningProtocolIds.Contains(protocol.Id))
                        protocol.Start();
            }
        }        

        public void RegisterProtocol(Protocol protocol)
        {
            lock (_locker)
                if (!_stopped && !_registeredProtocols.Contains(protocol))
                {
                    _registeredProtocols.Add(protocol);
                    Save();
                }
        }

        #region callback scheduling
        public string ScheduleRepeatingCallback(Action<CancellationToken> callback, int initialDelayMS, int repeatDelayMS)
        {
            return ScheduleRepeatingCallback(callback, initialDelayMS, repeatDelayMS, null);
        }

        public string ScheduleRepeatingCallback(Action<CancellationToken> callback, int initialDelayMS, int repeatDelayMS, string userNotificationMessage)
        {
            lock (_idCallbackCancellerMessage)
            {
                string callbackId = AddCallback(callback, userNotificationMessage);
                ScheduleRepeatingCallback(callbackId, initialDelayMS, repeatDelayMS, userNotificationMessage);
                return callbackId;
            }
        }

        public string ScheduleOneTimeCallback(Action<CancellationToken> callback, int delay)
        {
            return ScheduleOneTimeCallback(callback, delay, null);
        }

        public string ScheduleOneTimeCallback(Action<CancellationToken> callback, int delay, string userNotificationMessage)
        {
            lock (_idCallbackCancellerMessage)
            {
                string callbackId = AddCallback(callback, userNotificationMessage);
                ScheduleOneTimeCallback(callbackId, delay, userNotificationMessage);
                return callbackId;
            }
        }

        private string AddCallback(Action<CancellationToken> callback, string userNotificationMessage)
        {
            lock (_idCallbackCancellerMessage)
            {
                string callbackId = Guid.NewGuid().ToString();
                _idCallbackCancellerMessage.Add(callbackId, new Tuple<Action<CancellationToken>, CancellationTokenSource, string>(callback, null, userNotificationMessage));
                return callbackId;
            }
        }

        public bool CallbackIsScheduled(string callbackId)
        {
            lock(_idCallbackCancellerMessage)
                return _idCallbackCancellerMessage.ContainsKey(callbackId);
        }

        public string RescheduleRepeatingCallback(string callbackId, int initialDelayMS, int repeatDelayMS)
        {
            lock (_idCallbackCancellerMessage)
            {
                Tuple<Action<CancellationToken>, CancellationTokenSource, string> callbackCancellerMessage;
                if (_idCallbackCancellerMessage.TryGetValue(callbackId, out callbackCancellerMessage))
                {
                    UnscheduleRepeatingCallback(callbackId);
                    return ScheduleRepeatingCallback(callbackCancellerMessage.Item1, initialDelayMS, repeatDelayMS, callbackCancellerMessage.Item3);
                }
                else
                    return null;
            }
        }

        public void RaiseCallbackAsync(string callbackId, bool repeating, bool notifyUser)
        {
            RaiseCallbackAsync(callbackId, repeating, notifyUser, null);
        }    

        public void RaiseCallbackAsync(string callbackId, bool repeating, bool notifyUser, Action callback)
        {
            lock (_idCallbackCancellerMessage)
            {
                // do we have callback information for the passed callbackId? we might not, in the case where the callback is canceled by the user and the system fires it subsequently.
                Tuple<Action<CancellationToken>, CancellationTokenSource, string> callbackCancellerMessage;
                if (_idCallbackCancellerMessage.TryGetValue(callbackId, out callbackCancellerMessage))
                {
                    KeepDeviceAwake();  // not all OSs support this (e.g., iOS), but call it anyway

                    new Thread(() =>
                        {
                            Action<CancellationToken> callbackToRaise = callbackCancellerMessage.Item1;
                            string userNotificationMessage = callbackCancellerMessage.Item3;

                            // callbacks cannot be raised concurrently -- drop the current callback if it is already in progress.
                            if (Monitor.TryEnter(callbackToRaise))
                            {
                                // initialize a new cancellation token source for this call, since they cannot be reset
                                CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();

                                // set cancellation token source in collection, so that someone can call CancelRaisedCallback
                                lock (_idCallbackCancellerMessage)
                                    _idCallbackCancellerMessage[callbackId] = new Tuple<Action<CancellationToken>, CancellationTokenSource, string>(callbackToRaise, cancellationTokenSource, userNotificationMessage);

                                try
                                {
                                    _logger.Log("Raising callback " + callbackId, LoggingLevel.Debug, GetType());

                                    if(notifyUser)
                                        IssueNotificationAsync(userNotificationMessage, callbackId);

                                    callbackToRaise(cancellationTokenSource.Token);
                                }
                                catch (Exception ex)
                                {
                                    _logger.Log("Callback failed:  " + ex.Message, LoggingLevel.Normal, GetType());
                                }
                                finally
                                {
                                    Monitor.Exit(callbackToRaise);

                                    // reset cancellation token to null since there is nothing to cancel
                                    lock (_idCallbackCancellerMessage)
                                        _idCallbackCancellerMessage[callbackId] = new Tuple<Action<CancellationToken>, CancellationTokenSource, string>(callbackToRaise, null, userNotificationMessage);
                                }
                            }
                            else
                                _logger.Log("Callback " + callbackId + " was already running. Not running again.", LoggingLevel.Debug, GetType());

                            if (!repeating)
                                lock (_idCallbackCancellerMessage)
                                    _idCallbackCancellerMessage.Remove(callbackId);

                            LetDeviceSleep();

                            if (callback != null)
                                callback();

                        }).Start();                           
                }
            }
        }

        public void CancelRaisedCallback(string callbackId)
        {
            lock (_idCallbackCancellerMessage)
            {
                Tuple<Action<CancellationToken>, CancellationTokenSource, string> callbackCancellerMessage;
                if (_idCallbackCancellerMessage.TryGetValue(callbackId, out callbackCancellerMessage) && callbackCancellerMessage.Item2 != null)  // the cancellation source will be null if the callback is not currently being raised
                    callbackCancellerMessage.Item2.Cancel();
            }
        }

        public void UnscheduleOneTimeCallback(string callbackId)
        {
            lock (_idCallbackCancellerMessage)
            {
                if (callbackId != null)
                {
                    _idCallbackCancellerMessage.Remove(callbackId);
                    UnscheduleCallback(callbackId, false);
                }
            }
        }

        public void UnscheduleRepeatingCallback(string callbackId)
        {                          
            lock (_idCallbackCancellerMessage)
            {
                if (callbackId != null)
                {
                    _idCallbackCancellerMessage.Remove(callbackId);
                    UnscheduleCallback(callbackId, true);
                }
            }
        }
        #endregion

        public void TextToSpeechAsync(string text)
        {
            TextToSpeechAsync(text, () =>
                {
                });                        
        }

        public void FlashNotificationAsync(string message)
        {
            FlashNotificationAsync(message, () =>
                {
                });
        }

        public void TestHealth(CancellationToken cancellationToken)
        {
            lock (_locker)
            {
                if (_stopped)
                    return;

                _logger.Log("Sensus health test is running (test " + ++_healthTestCount + ")", LoggingLevel.Normal, GetType());

                foreach (Protocol protocol in _registeredProtocols)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;
                    
                    if (_runningProtocolIds.Contains(protocol.Id))
                    {
                        protocol.TestHealth();

                        if (_healthTestCount % _healthTestsPerProtocolReport == 0)
                            protocol.StoreMostRecentProtocolReport();
                    }
                }
            }
        }

        public virtual void OnSleep()
        {
            _logger.Log("About to sleep.", LoggingLevel.Normal, GetType());

            Save();
        }

        public void UnregisterProtocol(Protocol protocol)
        {
            lock (_locker)
            {
                protocol.Stop();
                _registeredProtocols.Remove(protocol);
                Save();
            }
        }

        /// <summary>
        /// Stops the service helper, but leaves it in a state in which subsequent calls to Start will succeed. This happens, for example, when the service is stopped and then 
        /// restarted without being destroyed.
        /// </summary>
        public void StopAsync()
        {
            new Thread(() =>
                {
                    Stop();

                }).Start();
        }

        public void Stop()
        {
            lock (_locker)
            {
                if (_stopped)
                    return;

                _logger.Log("Stopping Sensus service.", LoggingLevel.Normal, GetType());

                foreach (Protocol protocol in _registeredProtocols)
                    protocol.Stop();

                _stopped = true;
            }

            // let others (e.g., platform-specific services and applications) know that we've stopped
            if (Stopped != null)
                Stopped(null, null);
        }

        public string GetSharePath(string extension)
        {
            lock (_locker)
            {
                int fileNum = 0;
                string path = null;
                while (path == null || File.Exists(path))
                    path = Path.Combine(_shareDirectory, fileNum++ + (string.IsNullOrWhiteSpace(extension) ? "" : "." + extension.Trim('.')));

                return path;
            }
        }

        public virtual void Destroy()
        {
            Dispose();           
        }           

        public void Dispose()
        {
            try
            {
                Stop();
            }
            catch (Exception ex)
            {
                Console.Out.WriteLine("Failed to stop service helper:  " + ex.Message);
            }

            try
            {
                _logger.Close();
            }
            catch (Exception ex)
            {
                Console.Out.WriteLine("Failed to close logger:  " + ex.Message);
            }

            _md5Hash.Dispose();
            _md5Hash = null;

            _singleton = null;
        }
    }
}
