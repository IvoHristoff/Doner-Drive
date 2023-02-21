using System.Collections;
using uk.vroad.api;
using uk.vroad.api.etc;
using uk.vroad.api.xmpl;
using uk.vroad.apk;
using uk.vroad.pac;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Networking;

namespace uk.vroad.xmpl
{
    public class URunTraffic: MonoBehaviour, Reporter.IExternalReporter
    {
        public static URunTraffic MostRecentInstance { get; private set;  }

        public string vRoadFilePath;
        public Text buildErrorText;
        public Slider vRoadSlider;
        public int simulationWorkers = AppTools.N_WORKERS_INIT;
        
        private App app;
        private bool hasUI;
        private bool loadInProgress;
        private string progressActivity;
        private int progressRaw;
        private int progressSmoothed;
#if UNITY_ANDROID || UNITY_WEBGL
        private string vroadDirPath;
#endif
#if UNITY_WEBGL
        public Slider speedSlider;
        public Text playPauseText;
        
        private UPlaySimExample playSim;
        private bool simRunning;
        private int stepsToSkip;
        private int skipCount;
#endif


        public void SetupTraffic(string path)
        {
            vRoadFilePath = path;
        }

        protected virtual void Awake()
        {
            app = ExampleApp.AwakeInstance();
            
            MostRecentInstance = this;
            hasUI = buildErrorText != null && vRoadSlider != null;

#if UNITY_ANDROID 
            if (hasUI) Reporter.SetExternalReporter(this);
#endif
            
#if UNITY_ANDROID || UNITY_WEBGL
            vroadDirPath = Application.persistentDataPath + "/vroad/";
#endif
          
            KFile mapFile = FindMapFile();
           
#if UNITY_ANDROID || UNITY_WEBGL
            if (mapFile == null) 
            { 
                StartCoroutine(FetchMapFileAsyncThenCopyAndLoad());

                return; // load after (async) fetch and copy; exit here
            }
#endif
            if (mapFile != null)
            {
                StartLoad(mapFile, 1); // .vroad
            }
            else
            {
                Reporter.Report("Failed to start. No VRoad file "+vRoadFilePath);
            }
        }
#if UNITY_WEBGL
        void Start()
        {
            playSim = UPlaySimExample.MostRecentInstance;
            playSim.SetFfwd(true);
            
            SetSimSpeed();
        }
#endif
        
        public void Report(string s)
        {
            if (hasUI) buildErrorText.text = s;
            
            // Debug.Log(s); // Should not need this as this is external report and standard Report sends to Debug.Log
        }
        
       

#if UNITY_ANDROID || UNITY_WEBGL
        
        private IEnumerator FetchMapFileAsyncThenCopyAndLoad()
        {
#if UNITY_ANDROID
            // Any files stored in Assets/StreamingAssets will be included in APK in subfolder /assets/
            // These can only be accessed using a web request (as if they were being downloaded from web)
            // The APK is a compressed jar file.
            string relURL = "jar:file://" + Application.dataPath + "!/assets/" + vRoadFilePath;
           
#endif
            
#if UNITY_WEBGL
#if UNITY_EDITOR
            string relURL = Application.dataPath + "/EditorVRoadFolder/"+ vRoadFilePath;
#else
            string absURL = Application.absoluteURL;
            string relURL;
            if (absURL.Contains('?'))
            {
                string[] absUrlParts = absURL.Split('?');
                relURL = absUrlParts[1];

                // A single argument, a relative path to the .vroad file, which may be either
                // vrdata/vroadW/GB+City+Suburb_Ha+Lat+Long.vroad
                // vrdata/NNNN/vroadW/GB+City+Suburb_Ha+Lat+Long.vroad

                string[] pathComponents = relURL.Split('/');
                vRoadFilePath = pathComponents[pathComponents.Length - 1];
            }
            else relURL = "vrdata/vroadW/" + vRoadFilePath;
#endif
            // Debug.Log("WebGL VRoad maps URL = "+relURL);
#endif
            
            UnityWebRequest req = UnityWebRequest.Get(relURL);
            
            // Make async call ...
            yield return req.SendWebRequest();

            // ... return here on completion
            if (req.result == UnityWebRequest.Result.Success)
            { 

                // Copy the data into a (normal) local file that can be accessed by our API
                byte[] data = req.downloadHandler.data;
                

                KDir dir = new KDir(vroadDirPath);
                if (!dir.Exists()) dir.Create();
                
                KFile mapFile = new KFile(dir, vRoadFilePath);
                
                // This will delete any existing file of the same name
                KWriter.Write(mapFile, data);

                if (MapFileOK(mapFile)) StartLoad(mapFile, 0); 
                
                else Reporter.Report("Failed to copy VROAD file:"+vRoadFilePath);

            }
            else
            {
                Reporter.Report("UnityWebRequest Failed (" + req.error + ") " + relURL);
            }

        }
#endif
        
        private void StartLoad(KFile mapFile, int nui)
        {
            loadInProgress = true;
            progressRaw = 0;
            progressSmoothed = 0;
            progressActivity = "Loading Map";
            if (hasUI) vRoadSlider.gameObject.SetActive(true);
            VRoad.SetWorkerCount(simulationWorkers);
            
            Reporter.ProgressPartsUI(nui);
     
#if UNITY_WEBGL && !UNITY_EDITOR     
            VRoad.LoadBlocking(app, mapFile);
#else
                VRoad.Load(app, mapFile);
#endif
        }
        void FixedUpdate()
        {
            if (loadInProgress)
            {
                progressRaw = Reporter.ProgressTotal();

                if (progressRaw < 100)
                {
                    int diff = (progressRaw * 100) - progressSmoothed;
                    if (diff > 0)
                    {
                        int inc = diff > 1000? 100: diff > 500? 20: 5;
                        progressSmoothed += inc;
                        if (hasUI) vRoadSlider.value = progressSmoothed;
                    }
                }
                else
                {
                    if (hasUI) vRoadSlider.gameObject.SetActive(false);
                    loadInProgress = false;
                }
            }
                    
#if UNITY_WEBGL && !UNITY_EDITOR_X
            if (!loadInProgress && progressRaw > 99 && simRunning)
            {
                if (skipCount < stepsToSkip) skipCount++;
                else
                {
                    app.Sim().SimStep();
                    skipCount = 0;
                }
               
            }
#endif
        }

        public int Progress() { return progressRaw; }
        public string ProgressActivity() { return progressActivity; }

        private KFile FindMapFile()
        {
            // ** ALWAYS ** Call VroadWriteDir here (to initialize in correct thread)
            string userVRoadDirW = KEnv.VroadWriteDir();
            
            if (vRoadFilePath == null) return null;
            vRoadFilePath = vRoadFilePath.Trim();
            if (vRoadFilePath.Length < 5) return null;
            
#if UNITY_ANDROID || UNITY_WEBGL
            KFile mapFile = new KFile(vroadDirPath, vRoadFilePath);
            return MapFileOK(mapFile)? mapFile: null;
#else
            KFile mapFile = new KFile(vRoadFilePath); // absolute path
            if (MapFileOK(mapFile)) return mapFile;
            
            mapFile = new KFile(userVRoadDirW, vRoadFilePath);
            if (MapFileOK(mapFile)) return mapFile;
            
            mapFile = new KFile(KEnv.VroadReadDir(), vRoadFilePath);
            if (MapFileOK(mapFile)) return mapFile;
         
            Reporter.Report("File not OK: " + mapFile.FullPath());
            return null;
#endif
        }

        private bool MapFileOK(KFile mapFile)
        {
            return mapFile != null && mapFile.Exists() && AppTools.SuitableAppFile(mapFile);
        }
        
#if UNITY_WEBGL
        public void TogglePlayPause()
        {
            simRunning = !simRunning; // this is for single thread operation
            playSim.RunTraffic(simRunning);
            playPauseText.text = simRunning ? "Pause" : "Play";
            
            Time.timeScale = simRunning? playSim.unity_xRT: 0; // Stop animation
        }

        // Can be called from Slider set to (min == 0, max == 3, Whole number)
        public void SetSimSpeed()
        {
            if (speedSlider == null) return;
            // Slider should be set to (min == 0, max == 3, Whole number)
            int value = (int) System.Math.Round(speedSlider.value);
            
            // Fastest value means don't skip any time steps
            // Each value below fastest means skip another time step
            int max = (int) System.Math.Round(speedSlider.maxValue);

            stepsToSkip = max - value;
            skipCount = 0;
        }
#else
         // This could be accessed by a button in the UI, if the simulation is running in multiple threads
        public void TogglePlayPause()
        {
            ISim sim = app.Sim();
            if (sim == null) return;

            if (sim.IsRunning()) sim.Pause();
            else sim.Play();
        }


#endif

    }
}