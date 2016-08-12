using System.Collections.Generic;
using System.IO;
using System.Linq;
using PhotoshopFile;
using UnityEngine;
using UnityEditor;
using Object = UnityEngine.Object;

namespace subjectnerdagreement.psdexport
{
	public class PSDExporter
	{
		public enum ScaleDown
		{
			Default,
			Half,
			Quarter
		}

		public static List<int> GetExportLayers(PsdExportSettings settings, PsdFileInfo fileInfo)
		{
			List<int> exportLayers = new List<int>();
			foreach(var keypair in settings.layerSettings)
			{
				PsdExportSettings.LayerSetting layerSetting = keypair.Value;
				// Don't export if not set to export
				if(!layerSetting.doExport)
					continue;

				// Don't export if group is off
				var groupInfo = fileInfo.GetGroupByLayerIndex(layerSetting.layerIndex);
				if(groupInfo != null && !groupInfo.visible)
					continue;

				exportLayers.Add(layerSetting.layerIndex);
			}
			return exportLayers;
		}

		public static int GetExportCount(PsdExportSettings settings, PsdFileInfo fileInfo)
		{
			var exportLayers = GetExportLayers(settings, fileInfo);
			return exportLayers.Count;
		}

		public static void Export(PsdExportSettings settings, PsdFileInfo fileInfo, bool exportExisting = true)
		{
			List<int> layerIndices = GetExportLayers(settings, fileInfo);

			// If not going to export existing, filter out layers with existing files
			if(exportExisting == false)
			{
				layerIndices = layerIndices.Where(delegate(int layerIndex)
					{
						string filePath = GetLayerFilename(settings, layerIndex);
						// If file exists, don't export
						return !File.Exists(filePath);
					}).ToList();	
			}

			int exportCount = 0;
			foreach(int layerIndex in  layerIndices)
			{
				string infoString = string.Format("Importing {0} / {1} Layers", exportCount, layerIndices.Count);
				string fileString = string.Format("Importing PSD Layers: {0}", settings.Filename);

				float progress = exportCount / (float)layerIndices.Count;

				EditorUtility.DisplayProgressBar(fileString, infoString, progress);
				
				CreateSprite(settings, layerIndex);
				exportCount++;
			}

			EditorUtility.ClearProgressBar();
			settings.SaveMetaData();
			settings.SaveLayerMetaData();
		}

		public static Sprite CreateSprite(PsdExportSettings settings, int layerIndex)
		{
			var layer = settings.Psd.Layers[layerIndex];
			var layerSetting = settings.layerSettings[layerIndex];
			Texture2D tex = CreateTexture(layer, layerSetting);
			if(tex == null)
				return null;
			Sprite sprite = SaveAsset(settings, tex, layerIndex);
			Object.DestroyImmediate(tex);
			return sprite;
		}

		private static Texture2D CreateTexture(Layer layer, PsdExportSettings.LayerSetting setting)
		{
			if(setting.npot &&
			   ((int)layer.Rect.width == 0 || (int)layer.Rect.height == 0))
				return null;

			int textureWidth = (int)layer.Rect.width;
			int textureHeight = (int)layer.Rect.height;

			if(setting.npot)
			{
				textureWidth = (int)layer.Rect.width;
				textureHeight = (int)layer.Rect.height;
			}
			else
			{
				textureWidth = layer.PsdFile.ColumnCount;
				textureHeight = layer.PsdFile.RowCount;
			}

			Texture2D tex = new Texture2D(textureWidth, textureHeight, TextureFormat.RGBA32, true);
			Color32[] pixels = new Color32[tex.width * tex.height];

			Channel red = (from l in layer.Channels
			               where l.ID == 0
			               select l).First();
			Channel green = (from l in layer.Channels
			                 where l.ID == 1
			                 select l).First();
			Channel blue = (from l in layer.Channels
			                where l.ID == 2
			                select l).First();
			Channel alpha = layer.AlphaChannel;

			if(setting.npot)
			{
				for(int i = 0; i < pixels.Length; i++)
				{
					byte r = 0;
					if(i < red.ImageData.Length)
					{
						r = red.ImageData[i];
					}

					byte g = 0;
					if(i < green.ImageData.Length)
					{
						g = green.ImageData[i];
					}

					byte b = 0;
					if(i < blue.ImageData.Length)
					{
						b = blue.ImageData[i];
					}

					byte a = 255;
					if(alpha != null && i < alpha.ImageData.Length)
					{
						a = alpha.ImageData[i];
					}

					int mod = i % tex.width;
					int n = ((tex.width - mod - 1) + i) - mod;
					pixels[pixels.Length - n - 1] = new Color32(r, g, b, a);
				}

				tex.SetPixels32(pixels);
			}
			else
			{
				for(int i = 0; i < pixels.Length; i++)
				{
					pixels[i] = Color.clear;
				}
				tex.SetPixels32(pixels);

				var originWidth = (int)layer.Rect.width;
				var originHeight = (int)layer.Rect.height;
				Color32[] originPixels = new Color32[originWidth * originHeight];
				for(int i = 0; i < originPixels.Length; i++)
				{
					byte r = red.ImageData[i];
					byte g = green.ImageData[i];
					byte b = blue.ImageData[i];
					byte a = 255;
					if(alpha != null)
					{
						a = alpha.ImageData[i];
					}

					int mod = i % originWidth;
					int n = ((originWidth - mod - 1) + i) - mod;
					originPixels[originPixels.Length - n - 1] = new Color32(r, g, b, a);
				}
				tex.SetPixels32((int)layer.Rect.x, textureHeight - ((int)layer.Rect.y + originHeight), originWidth, originHeight, originPixels);
			}

			tex.Apply();

			return tex;
		}

		public static string GetLayerFilename(PsdExportSettings settings, int layerIndex)
		{
			// Strip out invalid characters from the file name
			string layerName = settings.Psd.Layers[layerIndex].Name;
			foreach(char invalidChar in Path.GetInvalidFileNameChars())
			{
				layerName = layerName.Replace(invalidChar, '-');
			}

			layerName = settings.GetLayerPath(layerName);

			return layerName;
		}

		private static Sprite SaveAsset(PsdExportSettings settings, Texture2D tex, int layer)
		{
			string assetPath = GetLayerFilename(settings, layer);

			// Setup scaling variables
			float pixelsToUnits = settings.PixelsToUnitSize;

			PsdExportSettings.LayerSetting layerSetting = settings.layerSettings[layer];

			// Apply global scaling, if any
			if(settings.ScaleBy > 0)
			{
				if(layerSetting.scaleByMosaic)
				{
					tex = ScaleTextureByMosaic(tex, settings.ScaleBy);
				}
				else
				{
					tex = ScaleTextureByMipmap(tex, settings.ScaleBy);
				}
			}

			// Then scale by layer scale
			if(layerSetting.scaleBy != ScaleDown.Default)
			{
				// By default, scale by half
				int scaleLevel = 1;

				pixelsToUnits = settings.PixelsToUnitSize / 2f;
				
				// Setting is actually scale by quarter
				if(layerSetting.scaleBy == ScaleDown.Quarter)
				{
					scaleLevel = 2;
					pixelsToUnits = settings.PixelsToUnitSize / 4f;
				}

				// Apply scaling
				if(layerSetting.scaleByMosaic)
				{
					tex = ScaleTextureByMosaic(tex, scaleLevel);
				}
				else
				{
					tex = ScaleTextureByMipmap(tex, scaleLevel);
				}
			}

			byte[] buf = tex.EncodeToPNG();
			File.WriteAllBytes(assetPath, buf);
			AssetDatabase.Refresh();

			// Load the texture so we can change the type
			var textureObj = AssetDatabase.LoadAssetAtPath(assetPath, typeof(Texture2D));

			// Get the texture importer for the asset
			TextureImporter textureImporter = (TextureImporter)AssetImporter.GetAtPath(assetPath);
			// Read out the texture import settings so import pivot point can be changed
			TextureImporterSettings importSetting = new TextureImporterSettings();
			textureImporter.ReadTextureSettings(importSetting);

			// Set the pivot import setting
			importSetting.spriteAlignment = (int)settings.Pivot;
			// But if layer setting has a different pivot, set as new pivot
			if(settings.Pivot != layerSetting.pivot)
				importSetting.spriteAlignment = (int)layerSetting.pivot;
			// Pivot settings are the same but custom, set the vector
			//else if (settings.Pivot == SpriteAlignment.Custom)
			//	importSetting.spritePivot = settings.PivotVector;

			importSetting.spritePixelsPerUnit = pixelsToUnits;
			// Set the rest of the texture settings
			textureImporter.textureType = TextureImporterType.Sprite;
			textureImporter.spriteImportMode = SpriteImportMode.Single;
			textureImporter.spritePackingTag = settings.PackingTag;
			// Write in the texture import settings
			textureImporter.SetTextureSettings(importSetting);

			EditorUtility.SetDirty(textureObj);
			AssetDatabase.WriteImportSettingsIfDirty(assetPath);
			AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

			return (Sprite)AssetDatabase.LoadAssetAtPath(assetPath, typeof(Sprite));
		}

		private static Texture2D ScaleTextureByMipmap(Texture2D tex, int mipLevel)
		{
			if(mipLevel < 0 || mipLevel > 2)
				return null;
			int width = Mathf.RoundToInt(tex.width / (mipLevel * 2));
			int height = Mathf.RoundToInt(tex.height / (mipLevel * 2));

			// Scaling down by abusing mip maps
			Texture2D resized = new Texture2D(width, height);
			resized.SetPixels32(tex.GetPixels32(mipLevel));
			resized.Apply();
			return resized;
		}

		private static Texture2D ScaleTextureByMosaic(Texture2D tex, int mipLevel)
		{
			if(mipLevel < 0 || mipLevel > 2)
				return null;

			int width = Mathf.RoundToInt(tex.width / (mipLevel * 2));
			int height = Mathf.RoundToInt(tex.height / (mipLevel * 2));

			Texture2D resized = new Texture2D(width, height);
			Color32[] reColors = new Color32[width*height];

			int resizeY = 0;
			int resizeX = 0;
			int effectWidth = (mipLevel * 2);
			for(int heightOfffset = 0; heightOfffset < tex.height; heightOfffset += effectWidth)
			{
				for(int widthOffset = 0; widthOffset < tex.width; widthOffset += effectWidth)
				{
					Dictionary<Color,int> dic = new Dictionary<Color, int>();

					for(int x = widthOffset; (x < widthOffset + effectWidth && x < tex.width); x++)
					{
						for(int y = heightOfffset; (y < heightOfffset + effectWidth && y < tex.height); y++)
						{
							var col = tex.GetPixel(x, y);
							if(!dic.ContainsKey(col))
							{
								dic.Add(col,1);
							}

							dic[col] += 1;
						}
					}

					if(resizeX < resized.width && resizeY < resized.height)
					{
						Color32 targetCol = Color.clear;
						int nowCnt = 0;
						foreach(var col in dic)
						{
							if(col.Value > nowCnt)
							{
								targetCol = col.Key;
								nowCnt = col.Value;
							}
						}
						reColors[(resized.width * resizeY) + resizeX] = targetCol;
					}
					resizeX++;
				}

				resizeX = 0;
				resizeY++;
			}

			resized.SetPixels32(reColors);
			resized.Apply();
			return resized;
		}
	}
}
