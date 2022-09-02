﻿using System;
using System.IO;
using GLTF.Schema;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;
using WrapMode = GLTF.Schema.WrapMode;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UnityGLTF
{
	public partial class GLTFSceneExporter
	{
		private void ExportImages(string outputPath)
		{
			for (int t = 0; t < _imageInfos.Count; ++t)
			{
				writeImageToDiskMarker.Begin();

				var image = _imageInfos[t].texture;
				var textureMapType = _imageInfos[t].textureMapType;
				var fileOutputPath = Path.Combine(outputPath, _imageInfos[t].outputPath);

				var canBeExportedFromDisk = _imageInfos[t].canBeExportedFromDisk;

				var dir = Path.GetDirectoryName(fileOutputPath);
				if (!Directory.Exists(dir))
					Directory.CreateDirectory(dir);

				bool wasAbleToExportTexture = false;
				if (canBeExportedFromDisk)
				{
					File.WriteAllBytes(fileOutputPath, GetTextureDataFromDisk(image));
				}

				if (!wasAbleToExportTexture)
				{
					switch (textureMapType)
					{
						case TextureMapType.MetallicGloss:
							ExportMetallicGlossTexture(image as Texture2D, fileOutputPath, true);
							break;
						case TextureMapType.MetallicGloss_DontConvert:
						case TextureMapType.Custom_Unknown:
						case TextureMapType.Occlusion:
							ExportMetallicGlossTexture(image as Texture2D, fileOutputPath, false);
							break;
						case TextureMapType.CubeMap:
							ExportCubeMapTexture(image as Cubemap, fileOutputPath);
							break;
						case TextureMapType.Bump:
							ExportNormalTexture(image as Texture2D, fileOutputPath);
							break;
						case TextureMapType.Main:
						case TextureMapType.Emission:
						default:
							ExportTexture(image as Texture2D, fileOutputPath);
							break;
					}
				}

				writeImageToDiskMarker.End();
			}
		}

		/// <summary>
		/// This converts Unity's metallic-gloss texture representation into GLTF's metallic-roughness specifications.
		/// Unity's metallic-gloss A channel (glossiness) is inverted and goes into GLTF's metallic-roughness G channel (roughness).
		/// Unity's metallic-gloss R channel (metallic) goes into GLTF's metallic-roughess B channel.
		/// </summary>
		/// <param name="texture">Unity's metallic-gloss texture to be exported</param>
		/// <param name="outputPath">The location to export the texture</param>
		private void ExportMetallicGlossTexture(Texture2D texture, string outputPath, bool swapMetalGlossChannels)
		{
			var destRenderTexture = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
			if (swapMetalGlossChannels)
				Graphics.Blit(texture, destRenderTexture, _metalGlossChannelSwapMaterial);
			else
				Graphics.Blit(texture, destRenderTexture);
			WriteRenderTextureToDiskAndRelease(destRenderTexture, outputPath, true);
		}

		/// <summary>
		/// This converts Unity's metallic-gloss texture representation into GLTF's metallic-roughness specifications.
		/// Unity's metallic-gloss A channel (glossiness) is inverted and goes into GLTF's metallic-roughness G channel (roughness).
		/// Unity's metallic-gloss R channel (metallic) goes into GLTF's metallic-roughess B channel.
		/// </summary>
		/// <param name="texture">Unity's metallic-gloss texture to be exported</param>
		/// <param name="outputPath">The location to export the texture</param>
		private void ExportCubeMapTexture(Cubemap texture, string outputPath)
		{
			var destRenderTexture = RenderTexture.GetTemporary(texture.width * 2, texture.height, 24);
			Graphics.Blit(texture, destRenderTexture, new Material(Shader.Find("Hidden/CubemapToEquirectangular")));
			WriteRenderTextureToDiskAndRelease(destRenderTexture, outputPath, false);
		}

		/// <summary>
		/// This export's the normal texture. If a texture is marked as a normal map, the values are stored in the A and G channel.
		/// To output the correct normal texture, the A channel is put into the R channel.
		/// </summary>
		/// <param name="texture">Unity's normal texture to be exported</param>
		/// <param name="outputPath">The location to export the texture</param>
		private void ExportNormalTexture(Texture2D texture, string outputPath)
		{
			var destRenderTexture = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
			var wr = GL.sRGBWrite;
			GL.sRGBWrite = true;
			Graphics.Blit(texture, destRenderTexture, _normalChannelMaterial);
			WriteRenderTextureToDiskAndRelease(destRenderTexture, outputPath, true);
			GL.sRGBWrite = wr;
		}

		private void ExportTexture(Texture2D texture, string outputPath)
		{
			var destRenderTexture = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
			Graphics.Blit(texture, destRenderTexture);
			WriteRenderTextureToDiskAndRelease(destRenderTexture, outputPath, false);
		}

		private void WriteRenderTextureToDiskAndRelease(RenderTexture destRenderTexture, string outputPath, bool linear)
		{
			RenderTexture.active = destRenderTexture;

			var exportTexture = new Texture2D(destRenderTexture.width, destRenderTexture.height, TextureFormat.ARGB32, false, linear);
			exportTexture.ReadPixels(new Rect(0, 0, destRenderTexture.width, destRenderTexture.height), 0, 0);
			exportTexture.Apply();

			var binaryData = outputPath.EndsWith(".jpg") ? exportTexture.EncodeToJPG(settings.DefaultJpegQuality) : exportTexture.EncodeToPNG();
			File.WriteAllBytes(outputPath, binaryData);

			RenderTexture.ReleaseTemporary(destRenderTexture);
			if (Application.isEditor)
				Object.DestroyImmediate(exportTexture);
			else
				Object.Destroy(exportTexture);
		}


		public TextureInfo ExportTextureInfo(Texture texture, TextureMapType textureMapType)
		{
			var info = new TextureInfo();

			info.Index = ExportTexture(texture, textureMapType);

			return info;
		}

		public TextureId ExportTexture(Texture textureObj, TextureMapType textureMapType)
		{
			TextureId id = GetTextureId(_root, textureObj);
			if (id != null)
			{
				return id;
			}

			var texture = new GLTFTexture();

			//If texture name not set give it a unique name using count
			if (textureObj.name == "")
			{
				textureObj.name = (_root.Textures.Count + 1).ToString();
			}

			if (settings.ExportNames)
			{
				texture.Name = textureObj.name;
			}

			if (_shouldUseInternalBuffer)
		    {
				texture.Source = ExportImageInternalBuffer(textureObj, textureMapType);
		    }
		    else
		    {
				texture.Source = ExportImage(textureObj, textureMapType);
		    }
			texture.Sampler = ExportSampler(textureObj, textureMapType);

			_textures.Add(textureObj);

			id = new TextureId
			{
				Id = _root.Textures.Count,
				Root = _root
			};

			_root.Textures.Add(texture);

			return id;
		}

		/// <summary>
		/// Used for determining the resulting file output path for external images (not internal buffer).
		/// Depends on various factors such as alpha channel, texture map type / conversion needs, and existing data on disk.
		/// Logic needs to match the one in ExportImageInternalBuffer.
		/// The actual export happens in ExportImages.
		/// </summary>
		/// <returns>The relative texture output path on disk, including extension</returns>
		private string GetImageOutputPath(Texture texture, TextureMapType textureMapType, out bool ableToExportFromDisk)
		{
			var imagePath = _exportOptions.TexturePathRetriever(texture);
			if (string.IsNullOrEmpty(imagePath))
			{
				imagePath = texture.name;
			}

			ableToExportFromDisk = false;
			bool textureHasAlpha = true;

			if (settings.TryExportTexturesFromDisk && CanGetTextureDataFromDisk(textureMapType, texture, out string path))
			{
				if (IsPng(path) || IsJpeg(path))
				{
					imagePath = path;
					ableToExportFromDisk = true;
				}
			}

			switch (textureMapType)
			{
				case TextureMapType.CubeMap:
					textureHasAlpha = true;
					break;
				case TextureMapType.MetallicGloss:
					textureHasAlpha = false;
					break;
				case TextureMapType.MetallicGloss_DontConvert:
				case TextureMapType.Light:
				case TextureMapType.Occlusion:
					textureHasAlpha = false;
					break;
				case TextureMapType.Bump:
					textureHasAlpha = false;
					break;
				default:
					textureHasAlpha = TextureHasAlphaChannel(texture);
					break;
			}

			var canExportAsJpeg = !textureHasAlpha && settings.UseTextureFileTypeHeuristic;
			var desiredExtension = canExportAsJpeg ? ".jpg" : ".png";

			if (!settings.ExportFullPath)
			{
				imagePath = Path.GetFileName(imagePath);
			}

			if (!ableToExportFromDisk)
			{
				imagePath = Path.ChangeExtension(imagePath, desiredExtension);
			}

			return imagePath;
		}

		private ImageId ExportImage(Texture texture, TextureMapType textureMapType)
		{
			ImageId id = GetImageId(_root, texture);
			if (id != null)
			{
				return id;
			}

			var image = new GLTFImage();

			if (ExportNames)
			{
				image.Name = texture.name;
			}

            if (texture.GetType() == typeof(RenderTexture))
            {
                Texture2D tempTexture = new Texture2D(texture.width, texture.height);
                tempTexture.name = texture.name;

                RenderTexture.active = texture as RenderTexture;
                tempTexture.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                tempTexture.Apply();
                texture = tempTexture;
            }

#if UNITY_2017_1_OR_NEWER
            if (texture.GetType() == typeof(CustomRenderTexture))
            {
                Texture2D tempTexture = new Texture2D(texture.width, texture.height);
                tempTexture.name = texture.name;

                RenderTexture.active = texture as CustomRenderTexture;
                tempTexture.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                tempTexture.Apply();
                texture = tempTexture;
            }
#endif

			var filenamePath = GetImageOutputPath(texture, textureMapType, out var canBeExportedFromDisk);

			// some characters such as # are allowed as part of an URI and are thus not escaped
			// by EscapeUriString. They need to be escaped if they're part of the filename though.
			image.Uri = Path.Combine(
				Uri.EscapeUriString(Path.GetDirectoryName(filenamePath).Replace("\\","/")),
				Uri.EscapeDataString(Path.GetFileName(filenamePath))
			).Replace("\\","/");

            _imageInfos.Add(new ImageInfo
			{
				texture = texture,
				textureMapType = textureMapType,
				outputPath = filenamePath,
				canBeExportedFromDisk = canBeExportedFromDisk,
			});

            id = new ImageId
			{
				Id = _root.Images.Count,
				Root = _root
			};

			_root.Images.Add(image);

			return id;
		}

		private bool CanGetTextureDataFromDisk(TextureMapType textureMapType, Texture texture, out string path)
		{
			path = null;

#if UNITY_EDITOR
			if (Application.isEditor && UnityEditor.AssetDatabase.Contains(texture))
			{
				path = UnityEditor.AssetDatabase.GetAssetPath(texture);
				var importer = AssetImporter.GetAtPath(path) as TextureImporter;
				if (importer?.textureShape != TextureImporterShape.Texture2D)
					return false;

				switch (textureMapType)
				{
					// if this is a normal map generated from greyscale, we shouldn't attempt to export from disk
					case TextureMapType.Bump:
						if (importer && importer.textureType == TextureImporterType.NormalMap && importer.convertToNormalmap)
							return false;
						break;
					// check if the texture contains an alpha channel; if yes, we shouldn't attempt to export from disk but instead convert.
					case TextureMapType.MetallicGloss:
					case TextureMapType.SpecGloss:
						if (importer && importer.DoesSourceTextureHaveAlpha())
							return false;
						break;
				}

				if (File.Exists(path))
				{
					if(AssetDatabase.GetMainAssetTypeAtPath(path) != typeof(Texture2D))
					{
						var ext = Path.GetExtension(path);
						path = path.Replace(ext, "-" + texture.name + ext);
					}
					return true;
				}
			}
#endif
			return false;
		}

		private byte[] GetTextureDataFromDisk(Texture texture)
		{
#if UNITY_EDITOR
			var path = UnityEditor.AssetDatabase.GetAssetPath(texture);

			if (File.Exists(path))
				return File.ReadAllBytes(path);
#endif
			return null;
		}

		bool TextureHasAlphaChannel(Texture sourceTexture)
		{
			var hasAlpha = false;

#if UNITY_EDITOR
			if (AssetDatabase.Contains(sourceTexture) && sourceTexture is Texture2D)
			{
				var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(sourceTexture)) as TextureImporter;
				if (importer)
				{
					switch (importer.alphaSource)
					{
						case TextureImporterAlphaSource.FromInput:
							hasAlpha = importer.DoesSourceTextureHaveAlpha();
							break;
						case TextureImporterAlphaSource.FromGrayScale:
							hasAlpha = true;
							break;
						case TextureImporterAlphaSource.None:
							hasAlpha = false;
							break;
					}
				}
			}
#endif

			UnityEngine.Experimental.Rendering.GraphicsFormat graphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.None;
#if !UNITY_2019_1_OR_NEWER
			if (sourceTexture is Texture2D tex2D)
				graphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormatUtility.GetGraphicsFormat(tex2D.format, true);
#else
			graphicsFormat = sourceTexture.graphicsFormat;
#endif
#if UNITY_2018_2_OR_NEWER
			if(graphicsFormat != UnityEngine.Experimental.Rendering.GraphicsFormat.None)
				hasAlpha |= UnityEngine.Experimental.Rendering.GraphicsFormatUtility.HasAlphaChannel(graphicsFormat);
#else
			hasAlpha = true;
#endif
			return hasAlpha;
		}

		private bool IsPng(string filename)
		{
			return Path.GetExtension(filename).EndsWith("png", StringComparison.InvariantCultureIgnoreCase);
		}

		private bool IsJpeg(string filename)
		{
			return Path.GetExtension(filename).EndsWith("jpg", StringComparison.InvariantCultureIgnoreCase) || Path.GetExtension(filename).EndsWith("jpeg", StringComparison.InvariantCultureIgnoreCase);
		}

#if UNITY_EDITOR
		private bool TryGetImporter<T>(Object obj, out T importer) where T : AssetImporter
		{
			if (EditorUtility.IsPersistent(obj))
			{
				var texturePath = AssetDatabase.GetAssetPath(obj);
				importer = AssetImporter.GetAtPath(texturePath) as T;
				return importer;
			}
			importer = null;
			return false;
		}
#endif

		private ImageId ExportImageInternalBuffer(UnityEngine.Texture texture, TextureMapType textureMapType)
		{
			const string PNGMimeType = "image/png";
			const string JPEGMimeType = "image/jpeg";

			if (texture == null)
		    {
				throw new NullReferenceException("texture can not be NULL.");
		    }

		    var image = new GLTFImage();
		    image.MimeType = PNGMimeType;

			AlignToBoundary(_bufferWriter.BaseStream, 0x00);
			uint byteOffset = CalculateAlignment((uint)_bufferWriter.BaseStream.Position, 4);

			bool wasAbleToExportFromDisk = false;
			bool textureHasAlpha = true;

			if (settings.TryExportTexturesFromDisk && CanGetTextureDataFromDisk(textureMapType, texture, out string path))
			{
				if (IsPng(path))
				{
					image.MimeType = PNGMimeType;
					var imageBytes = GetTextureDataFromDisk(texture);
					_bufferWriter.Write(imageBytes);
					wasAbleToExportFromDisk = true;
				}
				else if (IsJpeg(path))
				{
					image.MimeType = JPEGMimeType;
					var imageBytes = GetTextureDataFromDisk(texture);
					_bufferWriter.Write(imageBytes);
					wasAbleToExportFromDisk = true;
				}
				else
				{
					Debug.Log("Texture can't be exported from disk: " + path + ". Only PNG & JPEG are supported. The texture will be re-encoded as PNG.", texture);
				}
			}

			if (!wasAbleToExportFromDisk)
		    {
				var sRGB = true;

#if UNITY_EDITOR
				if (textureMapType == TextureMapType.Custom_Unknown)
				{
#if UNITY_EDITOR
					if (TryGetImporter<TextureImporter>(texture, out var importer))
					{
						if (!importer.sRGBTexture)
							sRGB = false;
						else
							sRGB = true;
					}
#endif
				}
#endif

				var format = sRGB ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear;

				// TODO we could make sure texture size is power-of-two here
				var destRenderTexture = RenderTexture.GetTemporary(texture.width, texture.height, 24, RenderTextureFormat.ARGB32, format);
				GL.sRGBWrite = sRGB;

				switch (textureMapType)
				{
					case TextureMapType.MetallicGloss:
						Graphics.Blit(texture, destRenderTexture, _metalGlossChannelSwapMaterial);
						textureHasAlpha = false;
						break;
					case TextureMapType.MetallicGloss_DontConvert:
					case TextureMapType.Light:
					case TextureMapType.Occlusion:
						GL.sRGBWrite = false; // seems we need to convert here, otherwise color space is wrong
						Graphics.Blit(texture, destRenderTexture);
						textureHasAlpha = false;
						break;
					case TextureMapType.Bump:
						// GL.sRGBWrite = false; // TODO check what we should do here. Needs tests!
						Graphics.Blit(texture, destRenderTexture, _normalChannelMaterial);
						textureHasAlpha = false;
						break;
					default:
						Graphics.Blit(texture, destRenderTexture);
						textureHasAlpha = TextureHasAlphaChannel(texture);
						break;
				}

				var exportTexture = new Texture2D(texture.width, texture.height, TextureFormat.ARGB32, false);
				exportTexture.ReadPixels(new Rect(0, 0, destRenderTexture.width, destRenderTexture.height), 0, 0);
				exportTexture.Apply();

				var canExportAsJpeg = !textureHasAlpha && settings.UseTextureFileTypeHeuristic;
				var imageData = canExportAsJpeg ? exportTexture.EncodeToJPG(settings.DefaultJpegQuality) : exportTexture.EncodeToPNG();
				image.MimeType = canExportAsJpeg ? JPEGMimeType : PNGMimeType;
				_bufferWriter.Write(imageData);

				RenderTexture.ReleaseTemporary(destRenderTexture);

				GL.sRGBWrite = false;
				if (Application.isEditor)
				{
					UnityEngine.Object.DestroyImmediate(exportTexture);
				}
				else
				{
					UnityEngine.Object.Destroy(exportTexture);
				}
		    }

			// // Check for potential warnings in GLTF validation
			// if (!Mathf.IsPowerOfTwo(texture.width) || !Mathf.IsPowerOfTwo(texture.height))
			// {
			// 	Debug.LogWarning("Validation Warning: " + "Image has non-power-of-two dimensions: " + texture.width + "x" + texture.height + ".", texture);
			// }

			uint byteLength = CalculateAlignment((uint)_bufferWriter.BaseStream.Position - byteOffset, 4);
			image.BufferView = ExportBufferView((uint)byteOffset, (uint)byteLength);

		    var id = new ImageId
		    {
				Id = _root.Images.Count,
				Root = _root
		    };
		    _root.Images.Add(image);

		    return id;
		}

		private SamplerId ExportSampler(Texture texture, TextureMapType textureMapType)
		{
			var samplerId = GetSamplerId(_root, texture);
			if (samplerId != null)
				return samplerId;

			var sampler = new Sampler();

			switch (texture.wrapMode)
			{
				case TextureWrapMode.Clamp:
					sampler.WrapS = WrapMode.ClampToEdge;
					sampler.WrapT = WrapMode.ClampToEdge;
					break;
				case TextureWrapMode.Repeat:
					sampler.WrapS = WrapMode.Repeat;
					sampler.WrapT = WrapMode.Repeat;
					break;
				case TextureWrapMode.Mirror:
					sampler.WrapS = WrapMode.MirroredRepeat;
					sampler.WrapT = WrapMode.MirroredRepeat;
					break;
				default:
					Debug.LogWarning("Unsupported Texture.wrapMode: " + texture.wrapMode, texture);
					sampler.WrapS = WrapMode.Repeat;
					sampler.WrapT = WrapMode.Repeat;
					break;
			}

			var mipmapCount = 1;
#if UNITY_2019_2_OR_NEWER
			mipmapCount = texture.mipmapCount;
#else
			if (texture is Texture2D tex2D) mipmapCount = tex2D.mipmapCount;
#endif
			if(mipmapCount > 1)
			{
				switch (texture.filterMode)
				{
					case FilterMode.Point:
						sampler.MinFilter = MinFilterMode.NearestMipmapNearest;
						sampler.MagFilter = MagFilterMode.Nearest;
						break;
					case FilterMode.Bilinear:
						sampler.MinFilter = MinFilterMode.LinearMipmapNearest;
						sampler.MagFilter = MagFilterMode.Linear;
						break;
					case FilterMode.Trilinear:
						sampler.MinFilter = MinFilterMode.LinearMipmapLinear;
						sampler.MagFilter = MagFilterMode.Linear;
						break;
					default:
						Debug.LogWarning("Unsupported Texture.filterMode: " + texture.filterMode, texture);
						sampler.MinFilter = MinFilterMode.LinearMipmapLinear;
						sampler.MagFilter = MagFilterMode.Linear;
						break;
				}
			}
			else
			{
				switch (texture.filterMode)
				{
					case FilterMode.Point:
						sampler.MinFilter = MinFilterMode.Nearest;
						sampler.MagFilter = MagFilterMode.Nearest;
						break;
					default:
						sampler.MinFilter = MinFilterMode.Linear;
						sampler.MagFilter = MagFilterMode.Linear;
						break;
				}
			}

			samplerId = new SamplerId
			{
				Id = _root.Samplers.Count,
				Root = _root
			};

			_root.Samplers.Add(sampler);

			return samplerId;
		}
	}
}
