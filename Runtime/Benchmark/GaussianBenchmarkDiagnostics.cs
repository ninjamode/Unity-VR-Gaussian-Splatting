using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Gaussians.ThreeD;
using UnityEngine;

namespace Gaussians.Benchmark
{
    /// <summary>Blocking readback and PNG encoding, exclusively outside timing trials.</summary>
    public sealed class GaussianBenchmarkDiagnostics : IDisposable
    {
        readonly GaussianSplat3DRenderer[] renderers;
        readonly string directory, manifest, countsPath;
        readonly int order;
        readonly GaussianBenchmarkTrial trial;
        static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
        static string Q(string s) => GaussianBenchmarkResults.Quote(s);
        static string N(float n) => n.ToString("R", Culture);

        public GaussianBenchmarkDiagnostics(GameObject subject, Camera camera, string output, GaussianBenchmarkTrial trial)
        {
            this.trial = trial;
            order = trial.executionOrder;
            directory = Path.Combine(output, "images", order.ToString("D4"));
            Directory.CreateDirectory(directory);
            manifest = Path.Combine(output, "images.csv");
            countsPath = Path.Combine(output, "rejections.csv");
            if (!File.Exists(manifest)) File.WriteAllText(manifest,
                "experiment,variant,view,sample,path,frame,width,height,x,y,z,qx,qy,qz,qw,color_space\n");
            if (!File.Exists(countsPath)) File.WriteAllText(countsPath,
                "experiment,variant,view,sample,renderer,frame,input,deleted,invalid_attributes,cutout,near_or_behind,opacity,distance,small_radius,invalid_axes,clip_w,survived_view,early_frustum,projected_frustum,sort_population,submitted_instances\n");
            renderers = subject.GetComponentsInChildren<GaussianSplat3DRenderer>().Where(r => r.isActiveAndEnabled).ToArray();
            if (renderers.Length == 0) throw new InvalidOperationException("Count diagnostics require an active 3D renderer.");
            try { foreach (var renderer in renderers) renderer.BeginBenchmarkDiagnostics(camera); }
            catch { Dispose(); throw; }
        }

        public void SetCaptureFrame(bool capture)
        {
            foreach (var renderer in renderers) renderer.SetBenchmarkCaptureFrame(capture);
        }

        public void Capture(Camera camera, int sample)
        {
            string prefix = string.Join(",", Q(trial.experiment), Q(trial.variant), Q(trial.view), sample.ToString(Culture));
            var countRows = new System.Text.StringBuilder();
            for (int i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                uint[] counts = renderer.ReadBenchmarkDiagnostics(out uint sorted, out uint submitted);
                countRows.AppendLine(string.Join(",", prefix, Q(i + ":" + renderer.name), Time.frameCount.ToString(Culture),
                    string.Join(",", counts.Select(c => c.ToString(Culture))), sorted.ToString(Culture), submitted.ToString(Culture)));
            }
            Texture2D image = null;
            try
            {
                // End-of-frame display capture preserves the actual URP/direct output path.
                image = ScreenCapture.CaptureScreenshotAsTexture();
                if (!image || image.width != trial.width || image.height != trial.height)
                    throw new InvalidOperationException("Diagnostic capture resolution changed.");
                string filename = sample.ToString("D5") + ".png";
                File.WriteAllBytes(Path.Combine(directory, filename), image.EncodeToPNG());
                var p = camera.transform.position; var r = camera.transform.rotation;
                File.AppendAllText(manifest, string.Join(",", prefix, Q("images/" + order.ToString("D4") + "/" + filename),
                    Time.frameCount.ToString(Culture), image.width.ToString(Culture), image.height.ToString(Culture),
                    N(p.x), N(p.y), N(p.z), N(r.x), N(r.y), N(r.z), N(r.w), Q("display RGB8; project " + QualitySettings.activeColorSpace)) + "\n");
                File.AppendAllText(countsPath, countRows.ToString());
            }
            finally { if (image) UnityEngine.Object.Destroy(image); }
        }

        public void Dispose()
        {
            if (renderers != null)
                foreach (var renderer in renderers) if (renderer) renderer.EndBenchmarkDiagnostics();
        }
    }
}
