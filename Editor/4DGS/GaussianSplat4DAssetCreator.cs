using System;
using System.IO;
using System.Linq;
using Gaussians.ThreeD;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Gaussians.FourD.Editor
{
    public sealed class GaussianSplat4DAssetCreator : EditorWindow
    {
        const string Pref = "net.kleinbeck.gaussians.4d.CreatorOutputFolder";
        [SerializeField] string m_Source, m_Output = "Assets/Gaussians/4D", m_Name;
        [SerializeField] Gaussian4DBackends m_Backends;
        [SerializeField] GaussianSplatSHStorage m_SH;
        [SerializeField] bool m_Morton = true;
        string m_LastSource, m_Error;
        JObject m_Manifest;
        Vector2 m_Scroll;

        [MenuItem("Tools/Gaussians/4D/Create Splat Asset")]
        public static void Open() => GetWindow<GaussianSplat4DAssetCreator>("4D Gaussian Asset").Show();
        void OnEnable() { m_Output=EditorPrefs.GetString(Pref,"Assets/Gaussians/4D"); minSize=new Vector2(470,480); }
        public static void OpenReimport(Gaussian4DImportSettings settings)
        {
            var window=GetWindow<GaussianSplat4DAssetCreator>("4D Gaussian Asset");
            string folder=Path.GetDirectoryName(AssetDatabase.GetAssetPath(settings)).Replace('\\','/');
            window.m_Source=settings.sourceDirectory;window.m_Output=Path.GetDirectoryName(folder).Replace('\\','/');window.m_Name=Path.GetFileName(folder);
            window.m_Backends=settings.backends;window.m_SH=settings.canonicalSH;window.m_Morton=settings.mortonOrder;window.m_LastSource=null;window.Show();
        }
        void FolderField(string label, ref string value)
        {
            using(new EditorGUILayout.HorizontalScope())
            {
                value=EditorGUILayout.TextField(label,value);
                if(GUILayout.Button("…",GUILayout.Width(28)))
                {
                    string chosen=EditorUtility.OpenFolderPanel(label,Directory.Exists(value)?value:"Assets","");
                    if(!string.IsNullOrEmpty(chosen)) value=FileUtil.GetProjectRelativePath(chosen) is string relative && !string.IsNullOrEmpty(relative)?relative:chosen;
                }
            }
        }
        void OnGUI()
        {
            m_Scroll=EditorGUILayout.BeginScrollView(m_Scroll);
            EditorGUILayout.LabelField("Input data",EditorStyles.boldLabel);
            FolderField("4D Bundle Folder",ref m_Source);
            if(m_LastSource!=m_Source)
            {
                m_LastSource=m_Source;m_Manifest=null;m_Error=null;
                try
                {
                    if(!string.IsNullOrWhiteSpace(m_Source))
                    {
                        m_Manifest=JObject.Parse(File.ReadAllText(Path.Combine(m_Source,"manifest.json")));
                        if((string)m_Manifest["format"]!="unity-gaussians.hexplane-4dgs") throw new InvalidDataException("Unsupported 4D bundle.");
                        if(string.IsNullOrEmpty(m_Name)) m_Name=new DirectoryInfo(m_Source).Name;
                    }
                }
                catch(Exception e) { m_Manifest=null;m_Error=e.Message; }
            }
            if(m_Manifest!=null) DrawInputSummary();
            EditorGUILayout.Space();EditorGUILayout.LabelField("Output",EditorStyles.boldLabel);
            string previous=m_Output;FolderField("Output Folder",ref m_Output);
            if(previous!=m_Output) EditorPrefs.SetString(Pref,m_Output);
            m_Name=EditorGUILayout.TextField("Model Folder",m_Name);
            m_Backends=(Gaussian4DBackends)EditorGUILayout.EnumPopup("Included Backends",m_Backends);
            m_SH=(GaussianSplatSHStorage)EditorGUILayout.EnumPopup(new GUIContent("Canonical SH","Controls standalone canonical GPU storage. Animated SH storage is selected on the 4D component."),m_SH);
            m_Morton=EditorGUILayout.Toggle(new GUIContent("Morton Order","Apply the same spatial ordering to canonical rendering and all deformation inputs."),m_Morton);
            if(m_Manifest!=null) DrawOutputSummary();
            string destination=(m_Output??"").TrimEnd('/')+"/"+m_Name;
            bool managed=AssetDatabase.LoadAssetAtPath<Gaussian4DImportSettings>(destination+"/ImportSettings.asset");
            if(managed) EditorGUILayout.HelpBox("Reimport updates this model while preserving asset references. Only recorded generated files are replaced or removed.",MessageType.Info);
            if(!string.IsNullOrEmpty(m_Error)) EditorGUILayout.HelpBox(m_Error,MessageType.Error);
            bool validName=!string.IsNullOrWhiteSpace(m_Name) && m_Name!="." && m_Name!=".." && m_Name.IndexOfAny(Path.GetInvalidFileNameChars())<0 && !m_Name.Contains('/') && !m_Name.Contains('\\');
            using(new EditorGUI.DisabledScope(m_Manifest==null||!validName))
                if(GUILayout.Button(managed?"Reimport Splat Asset":"Create Splat Asset"))
                {
                    try
                    {
                        EditorUtility.DisplayProgressBar("4D Gaussian import","Checking and preparing runtime data…",.1f);
                        var asset=GaussianSplat4DBundleImporter.Import(m_Source,destination,m_Backends,m_SH,m_Morton);
                        m_Error=null;EditorGUIUtility.PingObject(asset);
                    }
                    catch(Exception e) { m_Error=e.Message;Debug.LogException(e); }
                    finally { EditorUtility.ClearProgressBar(); }
                }
            EditorGUILayout.EndScrollView();
        }
        void DrawInputSummary()
        {
            EditorGUILayout.LabelField("Model",$"{(int)m_Manifest["gaussian_count"]:N0} splats · SH degree {(int)m_Manifest["sh_degree"]}");
            var tensors=((JObject)m_Manifest["tensors"]).Properties().ToArray();
            var planes=tensors.Where(p=>p.Name.Contains(".grid.grids.")).ToArray();
            var mlp=tensors.Where(p=>p.Name.StartsWith("weights/",StringComparison.Ordinal)&&!p.Name.Contains(".grid.")).ToArray();
            EditorGUILayout.LabelField("HexPlanes",$"{planes.Length/6} levels · {planes.Length} planes · {EditorUtility.FormatBytes(planes.Sum(p=>(long)p.Value["byte_length"]))}");
            EditorGUILayout.LabelField("MLP",$"{mlp.Sum(p=>(long)p.Value["byte_length"])/4:N0} parameters · {EditorUtility.FormatBytes(mlp.Sum(p=>(long)p.Value["byte_length"]))}");
        }
        void DrawOutputSummary()
        {
            int count=(int)m_Manifest["gaussian_count"],degree=(int)m_Manifest["sh_degree"],coefficients=(degree+1)*(degree+1);
            long canonical=(long)count*(48+4*GaussianSplat3DData.SHWordsPerSplat(coefficients,m_SH));
            long tensors=((JObject)m_Manifest["tensors"]).Properties().Where(p=>GaussianSplat4DBundleImporter.IsRuntimeTensor(m_Manifest,p.Name,m_Backends)).Sum(p=>(long)p.Value["byte_length"]);
            long onnx=0;string path=(string)m_Manifest["onnx"]?["path"];
            if(m_Backends!=Gaussian4DBackends.Native && path!=null)
            {
                var info=new FileInfo(GaussianSplat4DBundleImporter.CheckedPath(m_Source,path));if(info.Exists) onnx=info.Length;
            }
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Canonical GPU data",EditorUtility.FormatBytes(canonical));
            EditorGUILayout.LabelField("Runtime tensor payload",EditorUtility.FormatBytes(tensors));
            EditorGUILayout.LabelField("ONNX source",EditorUtility.FormatBytes(onnx));
            EditorGUILayout.LabelField("Estimated data on disk",EditorUtility.FormatBytes(canonical+tensors+onnx));
            EditorGUILayout.LabelField("Excludes Unity metadata/import caches. Inference scratch memory is additional.",EditorStyles.wordWrappedMiniLabel);
        }
    }
}
