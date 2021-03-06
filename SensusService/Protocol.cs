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
using SensusService.DataStores.Local;
using SensusService.DataStores.Remote;
using SensusService.Probes;
using SensusUI.UiProperties;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using Xamarin.Forms;
using SensusService.Anonymization;
using System.Linq;
using System.Reflection;
using SensusUI;

namespace SensusService
{
    /// <summary>
    /// Self-contained sensing design, comprising probes and data stores.
    /// </summary>
    public class Protocol
    {
        #region static members
        private static JsonSerializerSettings _jsonSerializerSettings = new JsonSerializerSettings
        {
            PreserveReferencesHandling = PreserveReferencesHandling.Objects,
            ReferenceLoopHandling = ReferenceLoopHandling.Serialize,
            TypeNameHandling = TypeNameHandling.All,
            ConstructorHandling = ConstructorHandling.AllowNonPublicDefaultConstructor
        };

        public static void DisplayFromWebUriAsync(Uri webURI)
        {
            new Thread(() =>
                {
                    try
                    {
                        WebClient downloadClient = new WebClient();

                        downloadClient.DownloadDataCompleted += (object sender, DownloadDataCompletedEventArgs e) =>
                        {
                            DisplayFromBytesAsync(e.Result);
                        };

                        downloadClient.DownloadStringAsync(webURI);
                    }
                    catch (Exception ex)
                    {
                        SensusServiceHelper.Get().Logger.Log("Failed to download Protocol from URI \"" + webURI + "\":  " + ex.Message + ". If this is an HTTPS URI, make sure the server's certificate is valid.", LoggingLevel.Normal, typeof(Protocol));
                    }

                }).Start();
        }

        public static void DisplayFromBytesAsync(byte[] bytes)
        {
            new Thread(() =>
                {
                    try
                    {
                        DisplayFromJsonAsync(SensusServiceHelper.AesDecrypt(bytes));
                    }
                    catch (Exception ex)
                    {
                        SensusServiceHelper.Get().Logger.Log("Failed to decrypt protocol from bytes:  " + ex.Message, LoggingLevel.Normal, typeof(Protocol));
                    }                        

                }).Start();
        }

        /// <summary>
        /// Converts JSON to a Protocol object. Private because Protocols should always be serialized as encrypted binary codes, and this function works with unencrypted strings (it's called in service of the former).
        /// </summary>
        /// <param name="json">JSON to deserialize.</param>
        private static void DisplayFromJsonAsync(string json)
        {
            // always deserialize protocols on the main thread (e.g., since a looper might be required for android)
            Device.BeginInvokeOnMainThread(async() =>
                {
                    try
                    {
                        Protocol protocol = JsonConvert.DeserializeObject<Protocol>(json, _jsonSerializerSettings);
                        protocol.StorageDirectory = null;
                        while (protocol.StorageDirectory == null)
                        {
                            protocol.Id = Guid.NewGuid().ToString();
                            string candidateStorageDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), protocol.Id);
                            if (!Directory.Exists(candidateStorageDirectory))
                            {
                                protocol.StorageDirectory = candidateStorageDirectory;
                                Directory.CreateDirectory(protocol.StorageDirectory);
                            }
                        }

                        UiBoundSensusServiceHelper.Get(true).RegisterProtocol(protocol);

                        await App.Current.MainPage.Navigation.PushAsync(new ProtocolsPage());
                    }
                    catch (Exception ex)
                    {
                        SensusServiceHelper.Get().Logger.Log("Failed to deserialize/display protocol from JSON:  " + ex.Message, LoggingLevel.Normal, typeof(Protocol));
                    }
                });
        }
        #endregion

        public event EventHandler<bool> ProtocolRunningChanged;

        private string _id;
        private string _name;
        private List<Probe> _probes;
        private bool _running;
        private LocalDataStore _localDataStore;
        private RemoteDataStore _remoteDataStore;
        private string _storageDirectory;
        private ProtocolReport _mostRecentReport;
        private bool _forceProtocolReportsToRemoteDataStore;
        private string _lockPasswordHash;
        private AnonymizedJsonContractResolver _jsonAnonymizer;
        private DateTimeOffset _firstStartTimestamp;
        private bool _shareable;

        private readonly object _locker = new object();

        public string Id
        {
            get { return _id; }
            set { _id = value; }
        }

        [EntryStringUiProperty("Name:", true, 1)]
        public string Name
        {
            get { return _name; }
            set { _name = value; }
        }

        public List<Probe> Probes
        {
            get { return _probes; }
            set { _probes = value; }
        }

        [JsonIgnore]
        public bool Running
        {
            get { return _running; }
            set
            {
                if (value != _running)
                {
                    if (value)
                        StartAsync();
                    else
                        StopAsync();
                }
            }
        }

        public LocalDataStore LocalDataStore
        {
            get { return _localDataStore; }
            set
            {
                if (value != _localDataStore)
                {
                    _localDataStore = value;
                    _localDataStore.Protocol = this;
                }
            }
        }

        public RemoteDataStore RemoteDataStore
        {
            get { return _remoteDataStore; }
            set
            {
                if (value != _remoteDataStore)
                {
                    _remoteDataStore = value;
                    _remoteDataStore.Protocol = this;
                }
            }
        }

        public string StorageDirectory
        {
            get { return _storageDirectory; }
            set { _storageDirectory = value; }
        }

        [JsonIgnore]
        public ProtocolReport MostRecentReport
        {
            get { return _mostRecentReport; }
            set { _mostRecentReport = value; }
        }

        [OnOffUiProperty("Force Reports to Remote:", true, 20)]
        public bool ForceProtocolReportsToRemoteDataStore
        {
            get { return _forceProtocolReportsToRemoteDataStore; }
            set { _forceProtocolReportsToRemoteDataStore = value; }
        }

        public string LockPasswordHash
        {
            get 
            {
                return _lockPasswordHash; 
            }
            set
            {
                _lockPasswordHash = value;
            }
        }

        public AnonymizedJsonContractResolver JsonAnonymizer
        {
            get { return _jsonAnonymizer; }
            set { _jsonAnonymizer = value; }
        }

        public DateTimeOffset FirstStartTimestamp
        {
            get
            {
                return _firstStartTimestamp;
            }
            set
            {
                _firstStartTimestamp = value;
            }
        }

        [OnOffUiProperty("Shareable:", true, 10)]
        public bool Shareable
        {
            get
            {
                return _shareable;
            }
            set
            {
                _shareable = value;
            }
        }
        

        /// <summary>
        /// For JSON deserialization
        /// </summary>
        private Protocol()
        {
            _running = false;
            _forceProtocolReportsToRemoteDataStore = false;
            _lockPasswordHash = "";
            _jsonAnonymizer = new AnonymizedJsonContractResolver(this);
            _firstStartTimestamp = DateTimeOffset.MinValue;
            _shareable = true;
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="name">Name of protocol.</param>
        public Protocol(string name)
            : this()
        {
            _name = name;

            while (_storageDirectory == null)
            {
                _id = Guid.NewGuid().ToString();
                string candidateStorageDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), _id);
                if (!Directory.Exists(candidateStorageDirectory))
                {
                    _storageDirectory = candidateStorageDirectory;
                    Directory.CreateDirectory(_storageDirectory);
                }
            }

            _probes = new List<Probe>();
            foreach (Probe probe in Probe.GetAll())
                if (SensusServiceHelper.Get().Use(probe))
                {
                    probe.Protocol = this;

                    // since the new probe was just bound to this protocol, we need to let this protocol know about this probe's default anonymization preferences.
                    foreach (PropertyInfo anonymizableProperty in probe.DatumType.GetProperties().Where(property => property.GetCustomAttribute<Anonymizable>() != null))
                    {
                        Anonymizable anonymizableAttribute = anonymizableProperty.GetCustomAttribute<Anonymizable>(true);
                        _jsonAnonymizer.SetAnonymizer(anonymizableProperty, anonymizableAttribute.DefaultAnonymizer);
                    }

                    _probes.Add(probe);
                }
        }

        public void Save(string path)
        {
            using (FileStream file = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                byte[] encryptedBytes = SensusServiceHelper.AesEncrypt(JsonConvert.SerializeObject(this, _jsonSerializerSettings));
                file.Write(encryptedBytes, 0, encryptedBytes.Length);
                file.Close();
            }
        }

        public void StartAsync()
        {
            new Thread(Start).Start();
        }

        public void Start()
        {
            lock (_locker)
            {
                if (_running)
                    return;
                else
                    _running = true;

                if (ProtocolRunningChanged != null)
                    ProtocolRunningChanged(this, _running);

                if (_firstStartTimestamp == DateTimeOffset.MinValue)
                    _firstStartTimestamp = DateTimeOffset.UtcNow;

                // let the service helper know that the current protocol is running (saves helper)
                SensusServiceHelper.Get().RegisterProtocol(this);
                SensusServiceHelper.Get().AddRunningProtocolId(_id);

                SensusServiceHelper.Get().Logger.Log("Starting probes for protocol " + _name + ".", LoggingLevel.Normal, GetType());
                int probesStarted = 0;
                foreach (Probe probe in _probes)
                    if (probe.Enabled)
                    {
                        try
                        {
                            probe.Start();
                            probesStarted++;
                        }
                        catch (Exception ex)
                        {
                            SensusServiceHelper.Get().Logger.Log("Failed to start probe \"" + probe.GetType().FullName + "\":" + ex.Message, LoggingLevel.Normal, GetType());
                        }
                    }

                bool stopProtocol = false;

                if (probesStarted > 0)
                {
                    try
                    {
                        _localDataStore.Start();

                        try { _remoteDataStore.Start(); }
                        catch (Exception ex)
                        {
                            SensusServiceHelper.Get().Logger.Log("Remote data store failed to start:  " + ex.Message, LoggingLevel.Normal, GetType());
                            stopProtocol = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        SensusServiceHelper.Get().Logger.Log("Local data store failed to start:  " + ex.Message, LoggingLevel.Normal, GetType());
                        stopProtocol = true;
                    }
                }
                else
                {
                    string message = "No probes have been enabled. Will not start protocol.";
                    SensusServiceHelper.Get().FlashNotificationAsync(message);
                    SensusServiceHelper.Get().Logger.Log(message, LoggingLevel.Normal, GetType());
                    stopProtocol = true;
                }

                if (stopProtocol)
                    Stop();
            }
        }

        public void TestHealthAsync()
        {
            TestHealthAsync(() => { });
        }

        public void TestHealthAsync(Action callback)
        {
            new Thread(() =>
                {
                    TestHealth();

                    if(callback != null)
                        callback();

                }).Start();
        }

        public void TestHealth()
        {
            lock (_locker)
            {
                string error = null;
                string warning = null;
                string misc = null;

                if (!_running)
                {
                    error += "Restarting protocol \"" + _name + "\"...";
                    try
                    {
                        Stop();
                        Start();
                    }
                    catch (Exception ex) { error += ex.Message + "..."; }

                    if (_running)
                        error += "restarted protocol." + Environment.NewLine;
                    else
                        error += "failed to restart protocol." + Environment.NewLine;
                }

                if (_running)
                {
                    if (_localDataStore == null)
                        error += "No local data store present on protocol." + Environment.NewLine;
                    else if (_localDataStore.TestHealth(ref error, ref warning, ref misc))
                    {
                        error += "Restarting local data store...";

                        try { _localDataStore.Restart(); }
                        catch (Exception ex) { error += ex.Message + "..."; }

                        if (!_localDataStore.Running)
                            error += "failed to restart local data store." + Environment.NewLine;
                    }

                    if (_remoteDataStore == null)
                        error += "No remote data store present on protocol." + Environment.NewLine;
                    else if (_remoteDataStore.TestHealth(ref error, ref warning, ref misc))
                    {
                        error += "Restarting remote data store...";

                        try { _remoteDataStore.Restart(); }
                        catch (Exception ex) { error += ex.Message + "..."; }

                        if (!_remoteDataStore.Running)
                            error += "failed to restart remote data store." + Environment.NewLine;
                    }

                    foreach (Probe probe in _probes)
                        if (probe.Enabled && probe.TestHealth(ref error, ref warning, ref misc))
                        {
                            error += "Restarting probe \"" + probe.GetType().FullName + "\"...";

                            try { probe.Restart(); }
                            catch (Exception ex) { error += ex.Message + "..."; }

                            if (!probe.Running)
                                error += "failed to restart probe \"" + probe.GetType().FullName + "\"." + Environment.NewLine;
                        }
                }

                _mostRecentReport = new ProtocolReport(DateTimeOffset.UtcNow, error, warning, misc);

                SensusServiceHelper.Get().Logger.Log("Protocol report:" + Environment.NewLine + _mostRecentReport, LoggingLevel.Normal, GetType());

                int runningProtocols = SensusServiceHelper.Get().RunningProtocolIds.Count;
                SensusServiceHelper.Get().UpdateApplicationStatus(runningProtocols + " protocol" + (runningProtocols == 1 ? " is " : "s are") + " running");
            }
        }

        public void StoreMostRecentProtocolReport()
        {
            lock (_locker)
                if (_mostRecentReport != null)
                {
                    SensusServiceHelper.Get().Logger.Log("Storing protocol report locally.", LoggingLevel.Verbose, GetType());
                    _localDataStore.AddNonProbeDatum(_mostRecentReport);

                    if (!_localDataStore.UploadToRemoteDataStore && _forceProtocolReportsToRemoteDataStore)
                    {
                        SensusServiceHelper.Get().Logger.Log("Local data aren't pushed to remote, so we're copying the report datum directly to the remote cache.", LoggingLevel.Verbose, GetType());
                        _remoteDataStore.AddNonProbeDatum(_mostRecentReport);
                    }
                }
        }

        public void StopAsync()
        {
            StopAsync(() => { });
        }

        public void StopAsync(Action callback)
        {
            new Thread(() =>
                {
                    Stop();

                    if(callback != null)
                        callback();

                }).Start();
        }

        public void Stop()
        {
            lock (_locker)
            {
                if (_running)
                    _running = false;
                else
                    return;

                if (ProtocolRunningChanged != null)
                    ProtocolRunningChanged(this, _running);

                SensusServiceHelper.Get().RemoveRunningProtocolId(_id);

                SensusServiceHelper.Get().Logger.Log("Stopping protocol \"" + _name + "\".", LoggingLevel.Normal, GetType());

                foreach (Probe probe in _probes)
                    if (probe.Running)
                        try 
                        {
                            probe.Stop(); 
                        }
                        catch (Exception ex)
                        {
                            SensusServiceHelper.Get().Logger.Log("Failed to stop " + probe.GetType().FullName + ":  " + ex.Message, LoggingLevel.Normal, GetType());
                        }

                if (_localDataStore != null && _localDataStore.Running)
                {
                    try { _localDataStore.Stop(); }
                    catch (Exception ex) { SensusServiceHelper.Get().Logger.Log("Failed to stop local data store:  " + ex.Message, LoggingLevel.Normal, GetType()); }
                }

                if (_remoteDataStore != null && _remoteDataStore.Running)
                {
                    try { _remoteDataStore.Stop(); }
                    catch (Exception ex) { SensusServiceHelper.Get().Logger.Log("Failed to stop remote data store:  " + ex.Message, LoggingLevel.Normal, GetType()); }
                }

                SensusServiceHelper.Get().Logger.Log("Stopped protocol \"" + _name + "\".", LoggingLevel.Normal, GetType());
            }
        }

        public override bool Equals(object obj)
        {
            return obj is Protocol && (obj as Protocol)._id == _id;
        }

        public override int GetHashCode()
        {
            return _id.GetHashCode();
        }
    }
}
