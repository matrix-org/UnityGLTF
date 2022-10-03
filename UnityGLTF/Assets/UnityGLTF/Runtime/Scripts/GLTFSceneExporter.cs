#if UNITY_EDITOR
#define ANIMATION_EXPORT_SUPPORTED
#endif

#if ANIMATION_EXPORT_SUPPORTED && (UNITY_ANIMATION || !UNITY_2019_1_OR_NEWER)
#define ANIMATION_SUPPORTED
#endif

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using GLTF.Schema;
using Unity.Profiling;
using UnityEngine;
using UnityGLTF.Extensions;
using WrapMode = GLTF.Schema.WrapMode;

namespace UnityGLTF
{
	public class ExportOptions
	{
		public bool TreatEmptyRootAsScene = false;
		public bool MergeClipsWithMatchingNames = false;
		public LayerMask ExportLayers = -1;
		public ILogger logger;
		internal readonly GLTFSettings settings;

		public ExportOptions() : this(GLTFSettings.GetOrCreateSettings()) { }
		
		public ExportOptions(GLTFSettings settings)
		{
			if (!settings) settings = GLTFSettings.GetOrCreateSettings();
			if (settings.UseMainCameraVisibility)
				ExportLayers = Camera.main ? Camera.main.cullingMask : -1;
			this.settings = settings;
		}

		public GLTFSceneExporter.RetrieveTexturePathDelegate TexturePathRetriever = (texture) => texture.name;
		public GLTFSceneExporter.AfterSceneExportDelegate AfterSceneExport;
		public GLTFSceneExporter.BeforeSceneExportDelegate BeforeSceneExport;
		public GLTFSceneExporter.AfterNodeExportDelegate AfterNodeExport;
		public GLTFSceneExporter.BeforeMaterialExportDelegate BeforeMaterialExport;
		public GLTFSceneExporter.AfterMaterialExportDelegate AfterMaterialExport;
	}

	public partial class GLTFSceneExporter
	{
		// Available export callbacks.
		// Callbacks can be either set statically (for exporters that register themselves)
		// or added in the ExportOptions.
		public delegate string RetrieveTexturePathDelegate(Texture texture);
		public delegate void BeforeSceneExportDelegate(GLTFSceneExporter exporter, GLTFRoot gltfRoot);
		public delegate void AfterSceneExportDelegate(GLTFSceneExporter exporter, GLTFRoot gltfRoot);
		public delegate void AfterNodeExportDelegate(GLTFSceneExporter exporter, GLTFRoot gltfRoot, Transform transform, Node node);

		private static ILogger Debug = UnityEngine.Debug.unityLogger;

		private enum IMAGETYPE
		{
			RGB,
			RGBA,
			R,
			G,
			B,
			A,
			G_INVERT
		}

		public enum TextureMapType
		{
			Main,
			Bump,
			SpecGloss,
			Emission,
			MetallicGloss,
			Light,
			Occlusion,
			MetallicGloss_DontConvert,
			CubeMap,
			Custom_Unknown
		}

		private struct ImageInfo
		{
			public Texture texture;
			public TextureMapType textureMapType;
			public string outputPath;
			public bool canBeExportedFromDisk;
		}

		public IReadOnlyList<Transform> RootTransforms => _rootTransforms;

		private Transform[] _rootTransforms;
		private GLTFRoot _root;
		private BufferId _bufferId;
		private GLTFBuffer _buffer;
		private List<ImageInfo> _imageInfos;
		private List<Texture> _textures;
		private Dictionary<int, int> _exportedMaterials;
		private List<(Transform tr, AnimationClip clip)> _animationClips;
		private bool _shouldUseInternalBufferForImages;
		private Dictionary<int, int> _exportedTransforms;
		private List<Transform> _animatedNodes;

		private int _exportLayerMask;
		private ExportOptions _exportOptions;

		private Material _metalGlossChannelSwapMaterial;
		private Material _normalChannelMaterial;

		private const uint MagicGLTF = 0x46546C67;
		private const uint Version = 2;
		private const uint MagicJson = 0x4E4F534A;
		private const uint MagicBin = 0x004E4942;
		private const int GLTFHeaderSize = 12;
		private const int SectionHeaderSize = 8;

		protected struct PrimKey
		{
			public bool Equals(PrimKey other)
			{
				if (!Equals(Mesh, other.Mesh)) return false;
				if (Materials == null && other.Materials == null) return true;
				if (!(Materials != null && other.Materials != null)) return false;
				if (!Equals(Materials.Length, other.Materials.Length)) return false;
				for (var i = 0; i < Materials.Length; i++)
				{
					if (!Equals(Materials[i], other.Materials[i])) return false;
				}

				return true;
			}

			public override bool Equals(object obj)
			{
				return obj is PrimKey other && Equals(other);
			}

			public override int GetHashCode()
			{
				unchecked
				{
					var code = (Mesh != null ? Mesh.GetHashCode() : 0) * 397;
					if (Materials != null)
					{
						code = code ^ Materials.Length.GetHashCode() * 397;
						foreach (var mat in Materials)
							code = (code ^ (mat != null ? mat.GetHashCode() : 0)) * 397;
					}

					return code;
				}
			}

			public Mesh Mesh;
			public Material[] Materials;
		}

		private readonly Dictionary<PrimKey, MeshId> _primOwner = new Dictionary<PrimKey, MeshId>();

		#region Settings

		// Settings
		// private static GLTFSettings settings => GLTFSettings.GetOrCreateSettings();
		private GLTFSettings settings => _exportOptions.settings;
		private bool ExportNames => settings.ExportNames;
		private bool RequireExtensions => settings.RequireExtensions;
		private bool ExportAnimations => settings.ExportAnimations;

		#endregion

#region Profiler Markers

		private static ProfilerMarker exportGltfMarker = new ProfilerMarker("Export glTF");
		private static ProfilerMarker gltfSerializationMarker = new ProfilerMarker("Serialize exported data");
		private static ProfilerMarker exportMeshMarker = new ProfilerMarker("Export Mesh");
		private static ProfilerMarker exportPrimitiveMarker = new ProfilerMarker("Export Primitive");
		private static ProfilerMarker exportBlendShapeMarker = new ProfilerMarker("Export BlendShape");
		private static ProfilerMarker exportSkinFromNodeMarker = new ProfilerMarker("Export Skin");
		private static ProfilerMarker exportSparseAccessorMarker = new ProfilerMarker("Export Sparse Accessor");
		private static ProfilerMarker exportNodeMarker = new ProfilerMarker("Export Node");
		private static ProfilerMarker afterNodeExportMarker = new ProfilerMarker("After Node Export (Callback)");
		private static ProfilerMarker exportAnimationFromNodeMarker = new ProfilerMarker("Export Animation from Node");
		private static ProfilerMarker convertClipToGLTFAnimationMarker = new ProfilerMarker("Convert Clip to GLTF Animation");
		private static ProfilerMarker beforeSceneExportMarker = new ProfilerMarker("Before Scene Export (Callback)");
		private static ProfilerMarker exportSceneMarker = new ProfilerMarker("Export Scene");
		private static ProfilerMarker afterMaterialExportMarker = new ProfilerMarker("After Material Export (Callback)");
		private static ProfilerMarker exportMaterialMarker = new ProfilerMarker("Export Material");
		private static ProfilerMarker beforeMaterialExportMarker = new ProfilerMarker("Before Material Export (Callback)");
		private static ProfilerMarker writeImageToDiskMarker = new ProfilerMarker("Export Image - Write to Disk");
		private static ProfilerMarker afterSceneExportMarker = new ProfilerMarker("After Scene Export (Callback)");

		private static ProfilerMarker exportAccessorMarker = new ProfilerMarker("Export Accessor");
		private static ProfilerMarker exportAccessorMatrix4x4ArrayMarker = new ProfilerMarker("Matrix4x4[]");
		private static ProfilerMarker exportAccessorVector4ArrayMarker = new ProfilerMarker("Vector4[]");
		private static ProfilerMarker exportAccessorUintArrayMarker = new ProfilerMarker("Uint[]");
		private static ProfilerMarker exportAccessorColorArrayMarker = new ProfilerMarker("Color[]");
		private static ProfilerMarker exportAccessorVector3ArrayMarker = new ProfilerMarker("Vector3[]");
		private static ProfilerMarker exportAccessorVector2ArrayMarker = new ProfilerMarker("Vector2[]");
		private static ProfilerMarker exportAccessorIntArrayIndicesMarker = new ProfilerMarker("int[] (Indices)");
		private static ProfilerMarker exportAccessorIntArrayMarker = new ProfilerMarker("int[]");
		private static ProfilerMarker exportAccessorFloatArrayMarker = new ProfilerMarker("float[]");
		private static ProfilerMarker exportAccessorByteArrayMarker = new ProfilerMarker("byte[]");

		private static ProfilerMarker exportAccessorMinMaxMarker = new ProfilerMarker("Calculate min/max");
		private static ProfilerMarker exportAccessorBufferWriteMarker = new ProfilerMarker("Buffer.Write");

		private static ProfilerMarker exportGltfInitMarker = new ProfilerMarker("Init glTF Export");
		private static ProfilerMarker gltfWriteOutMarker = new ProfilerMarker("Write glTF");
		private static ProfilerMarker gltfWriteJsonStreamMarker = new ProfilerMarker("Write JSON stream");
		private static ProfilerMarker gltfWriteBinaryStreamMarker = new ProfilerMarker("Write binary stream");

		private static ProfilerMarker addAnimationDataMarker = new ProfilerMarker("Add animation data to glTF");
		private static ProfilerMarker exportRotationAnimationDataMarker = new ProfilerMarker("Rotation Keyframes");
		private static ProfilerMarker exportPositionAnimationDataMarker = new ProfilerMarker("Position Keyframes");
		private static ProfilerMarker exportScaleAnimationDataMarker = new ProfilerMarker("Scale Keyframes");
		private static ProfilerMarker exportWeightsAnimationDataMarker = new ProfilerMarker("Weights Keyframes");
		private static ProfilerMarker removeAnimationUnneededKeyframesMarker = new ProfilerMarker("Simplify Keyframes");
		private static ProfilerMarker removeAnimationUnneededKeyframesInitMarker = new ProfilerMarker("Init");
		private static ProfilerMarker removeAnimationUnneededKeyframesCheckIdenticalMarker = new ProfilerMarker("Check Identical");
		private static ProfilerMarker removeAnimationUnneededKeyframesCheckIdenticalKeepMarker = new ProfilerMarker("Keep Keyframe");
		private static ProfilerMarker removeAnimationUnneededKeyframesFinalizeMarker = new ProfilerMarker("Finalize");

#endregion

		/// <summary>
		/// Create a GLTFExporter that exports out a transform
		/// </summary>
		/// <param name="rootTransforms">Root transform of object to export</param>
		[Obsolete("Please switch to GLTFSceneExporter(Transform[] rootTransforms, ExportOptions options).  This constructor is deprecated and will be removed in a future release.")]
		public GLTFSceneExporter(Transform[] rootTransforms, RetrieveTexturePathDelegate texturePathRetriever)
			: this(rootTransforms, new ExportOptions { TexturePathRetriever = texturePathRetriever })
		{
		}

		/// <summary>
		/// Create a GLTFExporter that exports out a transform
		/// </summary>
		/// <param name="rootTransforms">Root transform of object to export</param>
		/// <param name="options">Export Settings</param>
		public GLTFSceneExporter(Transform[] rootTransforms, ExportOptions options)
		{
			_exportOptions = options;
			if (options.logger != null)
				Debug = options.logger;
			else
				Debug = UnityEngine.Debug.unityLogger;

			_exportLayerMask = _exportOptions.ExportLayers;

			var metalGlossChannelSwapShader = Resources.Load("MetalGlossChannelSwap", typeof(Shader)) as Shader;
			_metalGlossChannelSwapMaterial = new Material(metalGlossChannelSwapShader);

			var normalChannelShader = Resources.Load("NormalChannel", typeof(Shader)) as Shader;
			_normalChannelMaterial = new Material(normalChannelShader);

			_rootTransforms = rootTransforms;

			_exportedTransforms = new Dictionary<int, int>();
			_exportedCameras = new Dictionary<int, int>();
			_exportedLights = new Dictionary<int, int>();
			_animatedNodes = new List<Transform>();
			_skinnedNodes = new List<Transform>();
			_bakedMeshes = new Dictionary<SkinnedMeshRenderer, UnityEngine.Mesh>();

			_root = new GLTFRoot
			{
				Accessors = new List<Accessor>(),
				Animations = new List<GLTFAnimation>(),
				Asset = new Asset
				{
					Version = "2.0",
					Generator = "UnityGLTF"
				},
				Buffers = new List<GLTFBuffer>(),
				BufferViews = new List<BufferView>(),
				Cameras = new List<GLTFCamera>(),
				Images = new List<GLTFImage>(),
				Materials = new List<GLTFMaterial>(),
				Meshes = new List<GLTFMesh>(),
				Nodes = new List<Node>(),
				Samplers = new List<Sampler>(),
				Scenes = new List<GLTFScene>(),
				Skins = new List<Skin>(),
				Textures = new List<GLTFTexture>()
			};

			_imageInfos = new List<ImageInfo>();
			_exportedMaterials = new Dictionary<int, int>();
			_textures = new List<Texture>();
			_animationClips = new List<(Transform, AnimationClip)>();

			_buffer = new GLTFBuffer();
			_bufferId = new BufferId
			{
				Id = _root.Buffers.Count,
				Root = _root
			};
			_root.Buffers.Add(_buffer);
		}

		/// <summary>
		/// Gets the root object of the exported GLTF
		/// </summary>
		/// <returns>Root parsed GLTF Json</returns>
		public GLTFRoot GetRoot()
		{
			return _root;
		}

		/// <summary>
		/// Writes a binary GLB file with filename at path.
		/// </summary>
		/// <param name="path">File path for saving the binary file</param>
		/// <param name="fileName">The name of the GLTF file</param>
		public void SaveGLB(string path, string fileName)
		{
			var fullPath = GetFileName(path, fileName, ".glb");
			var dirName = Path.GetDirectoryName(fullPath);
			if (dirName != null && !Directory.Exists(dirName))
				Directory.CreateDirectory(dirName);
			_shouldUseInternalBufferForImages = true;

			using (FileStream glbFile = new FileStream(fullPath, FileMode.Create))
			{
				SaveGLBToStream(glbFile, fileName);
			}

			if (!_shouldUseInternalBufferForImages)
			{
				ExportImages(path);
			}
		}

		/// <summary>
		/// In-memory GLB creation helper. Useful for platforms where no filesystem is available (e.g. WebGL).
		/// </summary>
		/// <param name="sceneName"></param>
		/// <returns></returns>
		public byte[] SaveGLBToByteArray(string sceneName)
		{
			_shouldUseInternalBufferForImages = true;
			using (var stream = new MemoryStream())
			{
				SaveGLBToStream(stream, sceneName);
				return stream.ToArray();
			}
		}

		/// <summary>
		/// Writes a binary GLB file into a stream (memory stream, filestream, ...)
		/// </summary>
		/// <param name="path">File path for saving the binary file</param>
		/// <param name="fileName">The name of the GLTF file</param>
		public void SaveGLBToStream(Stream stream, string sceneName)
		{
			exportGltfMarker.Begin();

			exportGltfInitMarker.Begin();
			Stream binStream = new MemoryStream();
			Stream jsonStream = new MemoryStream();
			_shouldUseInternalBufferForImages = true;

			_bufferWriter = new BinaryWriterWithLessAllocations(binStream);

			TextWriter jsonWriter = new StreamWriter(jsonStream, Encoding.ASCII);
			exportGltfInitMarker.End();

			beforeSceneExportMarker.Begin();
			_exportOptions.BeforeSceneExport?.Invoke(this, _root);
			BeforeSceneExport?.Invoke(this, _root);
			beforeSceneExportMarker.End();

			_root.Scene = ExportScene(sceneName, _rootTransforms);

			if (ExportAnimations)
			{
				ExportAnimation();
			}

			// Export skins
			for (int i = 0; i < _skinnedNodes.Count; ++i)
			{
				Transform t = _skinnedNodes[i];
				ExportSkinFromNode(t);
			}

			afterSceneExportMarker.Begin();
			if (_exportOptions.AfterSceneExport != null)
				_exportOptions.AfterSceneExport(this, _root);

			if (AfterSceneExport != null)
				AfterSceneExport.Invoke(this, _root);
			afterSceneExportMarker.End();

			animationPointerResolver?.Resolve(this);

			_buffer.ByteLength = CalculateAlignment((uint)_bufferWriter.BaseStream.Length, 4);

			gltfSerializationMarker.Begin();
			_root.Serialize(jsonWriter, true);
			gltfSerializationMarker.End();

			gltfWriteOutMarker.Begin();
			_bufferWriter.Flush();
			jsonWriter.Flush();

			// align to 4-byte boundary to comply with spec.
			AlignToBoundary(jsonStream);
			AlignToBoundary(binStream, 0x00);

			int glbLength = (int)(GLTFHeaderSize + SectionHeaderSize +
				jsonStream.Length + SectionHeaderSize + binStream.Length);

			BinaryWriter writer = new BinaryWriter(stream);

			// write header
			writer.Write(MagicGLTF);
			writer.Write(Version);
			writer.Write(glbLength);

			gltfWriteJsonStreamMarker.Begin();
			// write JSON chunk header.
			writer.Write((int)jsonStream.Length);
			writer.Write(MagicJson);

			jsonStream.Position = 0;
			CopyStream(jsonStream, writer);
			gltfWriteJsonStreamMarker.End();

			gltfWriteBinaryStreamMarker.Begin();
			writer.Write((int)binStream.Length);
			writer.Write(MagicBin);

			binStream.Position = 0;
			CopyStream(binStream, writer);
			gltfWriteBinaryStreamMarker.End();

			writer.Flush();

			gltfWriteOutMarker.End();
			exportGltfMarker.End();
		}

		/// <summary>
		/// Specifies the path and filename for the GLTF Json and binary
		/// </summary>
		/// <param name="path">File path for saving the GLTF and binary files</param>
		/// <param name="fileName">The name of the GLTF file</param>
		public void SaveGLTFandBin(string path, string fileName)
		{
			exportGltfMarker.Begin();

			exportGltfInitMarker.Begin();
			_shouldUseInternalBufferForImages = false;
			var toLower = fileName.ToLowerInvariant();
			if (toLower.EndsWith(".gltf"))
				fileName = fileName.Substring(0, fileName.Length - 5);
			if (toLower.EndsWith(".bin"))
				fileName = fileName.Substring(0, fileName.Length - 4);
			var fullPath = GetFileName(path, fileName, ".bin");
			var dirName = Path.GetDirectoryName(fullPath);
			if (dirName != null && !Directory.Exists(dirName))
				Directory.CreateDirectory(dirName);

			// sanitized file path can differ
			fileName = Path.GetFileNameWithoutExtension(fullPath);
			var binFile = File.Create(fullPath);

			_bufferWriter = new BinaryWriterWithLessAllocations(binFile);
			exportGltfInitMarker.End();

			beforeSceneExportMarker.Begin();
			_exportOptions.BeforeSceneExport?.Invoke(this, _root);
			BeforeSceneExport?.Invoke(this, _root);
			beforeSceneExportMarker.End();

			_root.Scene = ExportScene(fileName, _rootTransforms);

			if (ExportAnimations)
			{
				ExportAnimation();
			}

			// Export skins
			for (int i = 0; i < _skinnedNodes.Count; ++i)
			{
				Transform t = _skinnedNodes[i];
				ExportSkinFromNode(t);

				// updateProgress(EXPORT_STEP.SKINNING, i, _skinnedNodes.Count);
			}

			afterSceneExportMarker.Begin();
			if (_exportOptions.AfterSceneExport != null)
				_exportOptions.AfterSceneExport(this, _root);

			if (AfterSceneExport != null)
				AfterSceneExport.Invoke(this, _root);
			afterSceneExportMarker.End();

			animationPointerResolver?.Resolve(this);

			AlignToBoundary(_bufferWriter.BaseStream, 0x00);
			_buffer.Uri = fileName + ".bin";
			_buffer.ByteLength = CalculateAlignment((uint)_bufferWriter.BaseStream.Length, 4);

			var gltfFile = File.CreateText(Path.ChangeExtension(fullPath, ".gltf"));
			gltfSerializationMarker.Begin();
			_root.Serialize(gltfFile);
			gltfSerializationMarker.End();

			gltfWriteOutMarker.Begin();
#if WINDOWS_UWP
			gltfFile.Dispose();
			binFile.Dispose();
#else
			gltfFile.Close();
			binFile.Close();
#endif
			ExportImages(path);
			gltfWriteOutMarker.End();

			exportGltfMarker.End();
		}

		/// <summary>
		/// Ensures a specific file extension from an absolute path that may or may not already have that extension.
		/// </summary>
		/// <param name="absolutePathThatMayHaveExtension">Absolute path that may or may not already have the required extension</param>
		/// <param name="requiredExtension">The extension to ensure, with leading dot</param>
		/// <returns>An absolute path that has the required extension</returns>
		public static string GetFileName(string directory, string fileNameThatMayHaveExtension, string requiredExtension)
		{
			var absolutePathThatMayHaveExtension = Path.Combine(directory, EnsureValidFileName(fileNameThatMayHaveExtension));

			if (!requiredExtension.StartsWith(".", StringComparison.Ordinal))
				requiredExtension = "." + requiredExtension;

			if (!Path.GetExtension(absolutePathThatMayHaveExtension).Equals(requiredExtension, StringComparison.OrdinalIgnoreCase))
				return absolutePathThatMayHaveExtension + requiredExtension;

			return absolutePathThatMayHaveExtension;
		}

		/// <summary>
		/// Strip illegal chars and reserved words from a candidate filename (should not include the directory path)
		/// </summary>
		/// <remarks>
		/// http://stackoverflow.com/questions/309485/c-sharp-sanitize-file-name
		/// </remarks>
		private static string EnsureValidFileName(string filename)
		{
			var invalidChars = Regex.Escape(new string(Path.GetInvalidFileNameChars()));
			var invalidReStr = string.Format(@"[{0}]+", invalidChars);

			var reservedWords = new []
			{
				"CON", "PRN", "AUX", "CLOCK$", "NUL", "COM0", "COM1", "COM2", "COM3", "COM4",
				"COM5", "COM6", "COM7", "COM8", "COM9", "LPT0", "LPT1", "LPT2", "LPT3", "LPT4",
				"LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
			};

			var sanitisedNamePart = Regex.Replace(filename, invalidReStr, "_");
			foreach (var reservedWord in reservedWords)
			{
				var reservedWordPattern = string.Format("^{0}\\.", reservedWord);
				sanitisedNamePart = Regex.Replace(sanitisedNamePart, reservedWordPattern, "_reservedWord_.", RegexOptions.IgnoreCase);
			}

			return sanitisedNamePart;
		}

		public void DeclareExtensionUsage(string extension, bool isRequired=false)
		{
			if( _root.ExtensionsUsed == null ){
				_root.ExtensionsUsed = new List<string>();
			}
			if(!_root.ExtensionsUsed.Contains(extension))
			{
				_root.ExtensionsUsed.Add(extension);
			}

			if(isRequired){

				if( _root.ExtensionsRequired == null ){
					_root.ExtensionsRequired = new List<string>();
				}
				if( !_root.ExtensionsRequired.Contains(extension))
				{
					_root.ExtensionsRequired.Add(extension);
				}
			}
		}

		private bool ShouldExportTransform(Transform transform)
		{
			if (!settings.ExportDisabledGameObjects && !transform.gameObject.activeSelf) return false;
			if (settings.UseMainCameraVisibility && (_exportLayerMask >= 0 && _exportLayerMask != (_exportLayerMask | 1 << transform.gameObject.layer))) return false;
			if (transform.CompareTag("EditorOnly")) return false;
			return true;
		}

		private SceneId ExportScene(string name, Transform[] rootObjTransforms)
		{
			exportSceneMarker.Begin();

			var scene = new GLTFScene();

			if (ExportNames)
			{
				scene.Name = name;
			}

			if(_exportOptions.TreatEmptyRootAsScene)
			{
				// if we're exporting with a single object selected, that object can be the scene root, no need for an extra root node.
				if (rootObjTransforms.Length == 1 && rootObjTransforms[0].GetComponents<Component>().Length == 1) // single root with a single transform
				{
					var firstRoot = rootObjTransforms[0];
					var newRoots = new Transform[firstRoot.childCount];
					for (int i = 0; i < firstRoot.childCount; i++)
						newRoots[i] = firstRoot.GetChild(i);
					rootObjTransforms = newRoots;
				}
			}

			scene.Nodes = new List<NodeId>(rootObjTransforms.Length);
			foreach (var transform in rootObjTransforms)
			{
				// if(!ShouldExportTransform(transform)) continue;
				scene.Nodes.Add(ExportNode(transform));
			}

			_root.Scenes.Add(scene);

			exportSceneMarker.End();

			return new SceneId
			{
				Id = _root.Scenes.Count - 1,
				Root = _root
			};
		}

		private NodeId ExportNode(Transform nodeTransform)
		{
			exportNodeMarker.Begin();

			var node = new Node();

			if (ExportNames)
			{
				node.Name = nodeTransform.name;
			}

#if ANIMATION_SUPPORTED
			if (nodeTransform.GetComponent<UnityEngine.Animation>() || nodeTransform.GetComponent<UnityEngine.Animator>())
			{
				_animatedNodes.Add(nodeTransform);
			}
#endif
			if (nodeTransform.GetComponent<SkinnedMeshRenderer>() && ContainsValidRenderer(nodeTransform.gameObject))
			{
				_skinnedNodes.Add(nodeTransform);
			}

			// export camera attached to node
			Camera unityCamera = nodeTransform.GetComponent<Camera>();
			if (unityCamera != null && unityCamera.enabled)
			{
				node.Camera = ExportCamera(unityCamera);
			}

			Light unityLight = nodeTransform.GetComponent<Light>();
			if (unityLight != null && unityLight.enabled)
			{
				node.Light = ExportLight(unityLight);
			}

            if (unityLight != null || unityCamera != null)
            {
                node.SetUnityTransform(nodeTransform, true);
            }
            else
            {
                node.SetUnityTransform(nodeTransform, false);
            }

            var id = new NodeId
			{
				Id = _root.Nodes.Count,
				Root = _root
			};

			// Register nodes for animation parsing (could be disabled if animation is disabled)
			_exportedTransforms.Add(nodeTransform.GetInstanceID(), _root.Nodes.Count);

			_root.Nodes.Add(node);

			// children that are primitives get put in a mesh
			GameObject[] primitives, nonPrimitives;
			FilterPrimitives(nodeTransform, out primitives, out nonPrimitives);
			if (primitives.Length > 0)
			{
				node.Mesh = ExportMesh(nodeTransform.name, primitives);
				RegisterPrimitivesWithNode(node, primitives);
			}

			exportNodeMarker.End();

			// children that are not primitives get added as child nodes
			if (nonPrimitives.Length > 0)
			{
				node.Children = new List<NodeId>(nonPrimitives.Length);
				foreach (var child in nonPrimitives)
				{
					if(!ShouldExportTransform(child.transform)) continue;
					node.Children.Add(ExportNode(child.transform));
				}
			}

			// node export callback
			afterNodeExportMarker.Begin();
			_exportOptions.AfterNodeExport?.Invoke(this, _root, nodeTransform, node);
			AfterNodeExport?.Invoke(this, _root, nodeTransform, node);
			afterNodeExportMarker.End();

			return id;
		}

		private static bool ContainsValidRenderer(GameObject gameObject)
		{
			if(!gameObject) return false;
			var meshRenderer = gameObject.GetComponent<MeshRenderer>();
			var meshFilter = gameObject.GetComponent<MeshFilter>();
			var skinnedMeshRender = gameObject.GetComponent<SkinnedMeshRenderer>();
			var materials = meshRenderer ? meshRenderer.sharedMaterials : skinnedMeshRender ? skinnedMeshRender.sharedMaterials : null;
			var anyMaterialIsNonNull = false;
			if(materials != null)
				for (int i = 0; i < materials.Length; i++)
					anyMaterialIsNonNull |= materials[i];
			return (meshFilter && meshRenderer && meshRenderer.enabled) || (skinnedMeshRender && skinnedMeshRender.enabled) && anyMaterialIsNonNull;
		}

        private void FilterPrimitives(Transform transform, out GameObject[] primitives, out GameObject[] nonPrimitives)
		{
			var childCount = transform.childCount;
			var prims = new List<GameObject>(childCount + 1);
			var nonPrims = new List<GameObject>(childCount);

			// add another primitive if the root object also has a mesh
			if (transform.gameObject.activeSelf || settings.ExportDisabledGameObjects)
			{
				if (ContainsValidRenderer(transform.gameObject))
				{
					prims.Add(transform.gameObject);
				}
			}
			for (var i = 0; i < childCount; i++)
			{
				var go = transform.GetChild(i).gameObject;

				// This seems to be a performance optimization but results in transforms that are detected as "primitives" not being animated
				// if (IsPrimitive(go))
				// 	 prims.Add(go);
				// else
				nonPrims.Add(go);
			}

			primitives = prims.ToArray();
			nonPrimitives = nonPrims.ToArray();
		}

		private static bool IsPrimitive(GameObject gameObject)
		{
			/*
			 * Primitives have the following properties:
			 * - have no children
			 * - have no non-default local transform properties
			 * - have MeshFilter and MeshRenderer components OR has SkinnedMeshRenderer component
			 */
			return gameObject.transform.childCount == 0
				&& gameObject.transform.localPosition == Vector3.zero
				&& gameObject.transform.localRotation == Quaternion.identity
				&& gameObject.transform.localScale == Vector3.one
				&& ContainsValidRenderer(gameObject);
		}

		private void ExportAnimation()
		{
			for (int i = 0; i < _animatedNodes.Count; ++i)
			{
				Transform t = _animatedNodes[i];
				ExportAnimationFromNode(ref t);
			}
		}

#region Public API

		public int GetAnimationId(AnimationClip clip, Transform transform)
		{
			for (var i = 0; i < _animationClips.Count; i++)
			{
				var entry = _animationClips[i];
				if (entry.tr == transform && entry.clip == clip) return i;
			}
			return -1;
		}

		public MaterialId GetMaterialId(GLTFRoot root, Material materialObj)
		{
			if (_exportedMaterials.TryGetValue(materialObj.GetInstanceID(), out var id))
			{
				return new MaterialId
				{
					Id = id,
					Root = root
				};
			}

			return null;
		}

		public TextureId GetTextureId(GLTFRoot root, Texture textureObj)
		{
			for (var i = 0; i < _textures.Count; i++)
			{
				if (_textures[i] == textureObj)
				{
					return new TextureId
					{
						Id = i,
						Root = root
					};
				}
			}

			return null;
		}

		public ImageId GetImageId(GLTFRoot root, Texture imageObj)
		{
			for (var i = 0; i < _imageInfos.Count; i++)
			{
				if (_imageInfos[i].texture == imageObj)
				{
					return new ImageId
					{
						Id = i,
						Root = root
					};
				}
			}

			return null;
		}

		public SamplerId GetSamplerId(GLTFRoot root, Texture textureObj)
		{
			for (var i = 0; i < root.Samplers.Count; i++)
			{
				bool filterIsNearest = root.Samplers[i].MinFilter == MinFilterMode.Nearest
					|| root.Samplers[i].MinFilter == MinFilterMode.NearestMipmapNearest
					|| root.Samplers[i].MinFilter == MinFilterMode.LinearMipmapNearest;

				bool filterIsLinear = root.Samplers[i].MinFilter == MinFilterMode.Linear
					|| root.Samplers[i].MinFilter == MinFilterMode.NearestMipmapLinear;

				bool filterMatched = textureObj.filterMode == FilterMode.Point && filterIsNearest
					|| textureObj.filterMode == FilterMode.Bilinear && filterIsLinear
					|| textureObj.filterMode == FilterMode.Trilinear && root.Samplers[i].MinFilter == MinFilterMode.LinearMipmapLinear;

				bool wrapMatched = textureObj.wrapMode == TextureWrapMode.Clamp && root.Samplers[i].WrapS == WrapMode.ClampToEdge
					|| textureObj.wrapMode == TextureWrapMode.Repeat && root.Samplers[i].WrapS != WrapMode.ClampToEdge;

				if (filterMatched && wrapMatched)
				{
					return new SamplerId
					{
						Id = i,
						Root = root
					};
				}
			}

			return null;
		}

		public Texture GetTexture(int id) => _textures[id];

#endregion
	}
}
