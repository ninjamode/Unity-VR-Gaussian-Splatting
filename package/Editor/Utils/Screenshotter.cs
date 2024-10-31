using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEditor;
using Debug = UnityEngine.Debug;

namespace GaussianSplatting.Editor.Utils
{
    public class ScreenshotterGUI : EditorWindow
    {
        public int imageWidth = 2880;
        public int imageHeight = 1600;
        public string storagePath = "Assets/";
        
        public Transform targetObject = null;
        public Vector3 rotationAmount = Vector3.zero;
        
        private int camSelection = 0;


        [MenuItem("Tools/Screenshoter")]
        public static void Init()
        {
            var window = GetWindowWithRect<ScreenshotterGUI>(new Rect(50, 50, 360, 340), false,
                "Screenshotter", true);
            window.minSize = new Vector2(320, 320);
            window.maxSize = new Vector2(1500, 1500);
            window.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space();
            GUILayout.Label("Settings", EditorStyles.boldLabel);
            imageWidth = EditorGUILayout.IntField("Image Width", imageWidth);
            imageHeight = EditorGUILayout.IntField("Image height", imageHeight);
            storagePath = EditorGUILayout.TextField("Storage path", storagePath);

            var cameras = Camera.allCameras;
            cameras = cameras.Prepend(SceneView.GetAllSceneCameras()[0]).ToArray();
            camSelection =
                EditorGUILayout.Popup("Source camera", this.camSelection, cameras.Select(c => c.name).ToArray());

            EditorGUILayout.Space();
            if (GUILayout.Button("Take Shot"))
            {
                Screenshotter.Capture(cameras[camSelection], imageWidth, imageHeight, storagePath);
            }
            
            EditorGUILayout.Space();
            EditorGUILayout.Separator();
            GUILayout.Label("Capture around target", EditorStyles.boldLabel);
            targetObject = EditorGUILayout.ObjectField("Target Transform", targetObject, typeof(Transform), true) as Transform;
            rotationAmount = EditorGUILayout.Vector3Field("Capture shot every x degree", rotationAmount);


            GUI.enabled = targetObject != null && rotationAmount != Vector3.zero;
            EditorGUILayout.Space();
            if (GUILayout.Button("Capture Shots Around Target"))
            {
                RotatingShot(cameras[camSelection]);
            }
            GUI.enabled = true;
        }

        private void RotatingShot(Camera cam)
        {
            var originalRotation = targetObject.eulerAngles;
            for (Vector3 deg = Vector3.zero; Mathf.Max(deg.x, deg.y, deg.z) < 360; deg += rotationAmount)
            {
                targetObject.eulerAngles = originalRotation + deg;
                Screenshotter.Capture(cam, imageWidth, imageHeight, storagePath, silent:true);
            }
            targetObject.eulerAngles = originalRotation;
        }
    }
}

public class Screenshotter : MonoBehaviour
{
    // Take a "screenshot" of a camera's Render Texture.
    public static void Capture(Camera cam, int width, int height, string outPath = "", bool silent = false)
    {
        var originalTarget = cam.targetTexture;
        cam.targetTexture = new RenderTexture(width, height, 32);
        var snapshot = new Texture2D(width, height, TextureFormat.ARGB32, false);
        var watch = new Stopwatch();
        watch.Start();

        // Render the camera's view.
        cam.Render();
        
        watch.Stop();
        if (!silent) Debug.Log($"Rendering took {watch.Elapsed.TotalMilliseconds}ms");
        
        RenderTexture.active = cam.targetTexture;
        snapshot.ReadPixels(new Rect(0, 0, cam.targetTexture.width, cam.targetTexture.height), 0, 0);
        var bytes = snapshot.EncodeToPNG();

        if (Application.isEditor)
        {
            DestroyImmediate(snapshot);
        }
        else
        {
            Destroy(snapshot);      
        }
        
        cam.targetTexture = originalTarget;

        int counter = 0;
        string path;
        Directory.CreateDirectory(outPath);
        while(true)
        {
            path = System.IO.Path.Combine(outPath, $"snapshot_{counter:0000}.png" );
            if (!System.IO.File.Exists(path))
                break;
            ++counter;
        }
        System.IO.File.WriteAllBytes(path, bytes);
    }
}