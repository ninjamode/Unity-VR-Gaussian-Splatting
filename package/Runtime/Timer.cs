using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using GaussianSplatting.Runtime;
using UnityEngine;
using UnityEngine.XR;
using Debug = UnityEngine.Debug;

public class Timer : MonoBehaviour
{
    public bool timeToTexture = true;
    public bool timeFTM = false;
    
    public Camera cam;
    public GaussianSplatRenderer target;

    public bool nativeResolution = false;
    public int imageWidth = 4000;
    public int imageHeight = 2000;
    public uint numFrames = 5;
    public bool saveImage = true;
    public string outPath = "Assets/AnatomyShots";

    public int numViews = 64;

    void Update()
    {
        if (Time.frameCount == 500)
        {
            if (nativeResolution)
            {
                imageWidth = XRSettings.eyeTextureWidth;
                imageHeight = XRSettings.eyeTextureHeight;


                Debug.Log($"Rendering at {imageWidth} {imageHeight}");
            }
            
            if (timeToTexture) TimeRender();
        }

        if (Time.frameCount == 550)
        {
            if (timeFTM) StartCoroutine(CaptureFTM());
        }

    }
    
    private void TimeRender() 
    {
        Debug.Log($"Cam rendering timing started");
        target.gameObject.SetActive(true);
        
        var originalTarget = cam.targetTexture;
        cam.targetTexture = new RenderTexture(imageWidth, imageHeight, 32);
        var snapshot = new Texture2D(imageWidth, imageHeight, TextureFormat.ARGB32, false);
        var watch = new Stopwatch();

        var times = new Dictionary<int, List<double>>();
        var asset = target.asset;
        
        Texture2D blocker = new Texture2D(1, 1, TextureFormat.RGB24, false);
        outPath = Path.Join(Application.persistentDataPath, outPath);
        
        for (int tries = 0; tries < numFrames; tries++)
        {
            for (int i = 0; i < Mathf.Min(numViews, asset.cameras.Length); i += 1)
            {
                target.ActivateCamera(i);
                
                watch.Reset();
                watch.Start();

                for (int j = 0; j < numFrames * 3; j++)
                {
                    cam.Render();
                }
                RenderTexture.active = cam.targetTexture;
                blocker.ReadPixels(new Rect(0, 0, 1, 1), 0, 0);
                    
                watch.Stop();
                
                if (!times.TryGetValue(i, out var frametimes))
                {
                    frametimes = new List<double>(tries);
                    times[i] = frametimes;
                }
                frametimes.Add(watch.Elapsed.TotalMilliseconds / (numFrames*3));
                
                if (saveImage && tries == 0)
                {
                    RenderTexture.active = cam.targetTexture;
                    snapshot.ReadPixels(new Rect(0, 0, cam.targetTexture.width, cam.targetTexture.height), 0, 0);
                    var bytes = snapshot.EncodeToPNG();
            
                    int counter = 0;
                    string path;
                    Directory.CreateDirectory(outPath);
                    while(true)
                    {
                        path = System.IO.Path.Combine(outPath, $"img_{i}_{counter:0000}.png" );
                        if (!System.IO.File.Exists(path))
                            break;
                        ++counter;
                    }
                    System.IO.File.WriteAllBytes(path, bytes);
                }
            }
        }

        Debug.Log("Saving times to " + Path.Join(Application.persistentDataPath, "timings.csv"));
        foreach (var timings in times)
        {
            Debug.Log($"Rendering cam {timings.Key} took {timings.Value.Average()}ms, ({string.Join(",", timings.Value)})");
        }

        var timingsStr = times.Select(kvp => $"{kvp.Key},{string.Join(",", kvp.Value)}")
            .Prepend($"img,{string.Join(",", Enumerable.Range(0, (int)numFrames).Select(n => $"r_{n}"))}");
        File.WriteAllLines(Path.Join(Application.persistentDataPath, "timings.csv"), timingsStr);


        if (Application.isEditor)
        {
            DestroyImmediate(snapshot);
        }
        else
        {
            Destroy(snapshot);      
        }

        cam.targetTexture = originalTarget;
    }
    
    IEnumerator CaptureFTM()
    {
        Debug.Log($"FTM timing started");

        target.gameObject.SetActive(true);

        var originalTex = cam.targetTexture;
        if (!nativeResolution)
        {
            var renderTexture = new RenderTexture(imageWidth, imageHeight, 24);
            renderTexture.useMipMap = false;
            cam.targetTexture = renderTexture;
        }
        
        var times = new Dictionary<int, IEnumerable<string>>();
        var times2 = new Dictionary<int, string>();
        var asset = target.asset;

        for (int cam_idx = 0; cam_idx < Mathf.Min(numViews, asset.cameras.Length); cam_idx += 1)
        {
            target.ActivateCamera(cam_idx);

            for (int i = 0; i <= numFrames + 20; i++)
            {
                FrameTimingManager.CaptureFrameTimings();
                yield return null;
            }
            
            var frameTimings = new FrameTiming[numFrames];
            FrameTimingManager.GetLatestTimings(numFrames, frameTimings);

            var stringified = frameTimings.Select(ft => $"{ft.gpuFrameTime},{ft.cpuRenderThreadFrameTime}");
            times[cam_idx] = stringified;

            var frameStartTimestamp = string.Join(",", frameTimings.Select(ft => ft.frameStartTimestamp));
            var firstSubmitTimes = string.Join(",", frameTimings.Select(ft => ft.firstSubmitTimestamp));
            var cpuTimePresentCalled = string.Join(",", frameTimings.Select(ft => ft.cpuTimePresentCalled));
            var cpuTimeFrameComplete = string.Join(",", frameTimings.Select(ft => ft.cpuTimeFrameComplete));
            times2[cam_idx] = $"{frameStartTimestamp},{firstSubmitTimes},{cpuTimePresentCalled},{cpuTimeFrameComplete}";
        }

        cam.targetTexture = originalTex;

        Debug.Log("Saved times to " + Path.Join(Application.persistentDataPath, "timings*.csv"));

        var timingsStr = times.Select(kvp => $"{kvp.Key},{string.Join(",", kvp.Value)}")
            .Prepend($"img,{string.Join(",", Enumerable.Range(0, (int)numFrames).Select(n => $"gpu_{n},cpu_{n}"))}");
        var timingStr2 = times2.Select(kvp => $"{kvp.Key},{kvp.Value}")
            .Prepend($"img," +
                     $"{string.Join(",", Enumerable.Range(0, (int)numFrames).Select(n => $"frameStart_{n}"))}," +
                     $"{string.Join(",", Enumerable.Range(0, (int)numFrames).Select(n => $"firstSubmit_{n}"))}," +
                     $"{string.Join(",", Enumerable.Range(0, (int)numFrames).Select(n => $"cpuPresent_{n}"))}," +
                     $"{string.Join(",", Enumerable.Range(0, (int)numFrames).Select(n => $"cpuComplete_{n}"))}");
        File.WriteAllLines(Path.Join(Application.persistentDataPath, "timings_FTM.csv"), timingsStr);
        File.WriteAllLines(Path.Join(Application.persistentDataPath, "timings_FTM2.csv"), timingStr2);
    }
}
