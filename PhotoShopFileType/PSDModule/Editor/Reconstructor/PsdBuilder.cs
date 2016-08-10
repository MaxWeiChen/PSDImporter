using System;
using System.Collections.Generic;
using System.Linq;
using PhotoshopFile;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace subjectnerdagreement.psdexport
{
	public class PsdBuilder
	{
		#region convenience functions
		public static void BuildUiImages(GameObject root, PSDLayerGroupInfo group,
									PsdExportSettings settings, PsdFileInfo fileInfo,
									SpriteAlignment createAlign)
		{
			BuildPsd(root, group, settings, fileInfo,
					createAlign, new UiImgConstructor());
		}

		public static void BuildSprites(GameObject root, PSDLayerGroupInfo group,
										PsdExportSettings settings, PsdFileInfo fileInfo,
										SpriteAlignment createAlign)
		{
			BuildPsd(root, group, settings, fileInfo,
					createAlign, new SpriteConstructor());
		}
		#endregion

		#region General handler
		public static void BuildPsd(GameObject root, PSDLayerGroupInfo group,
									PsdExportSettings settings, PsdFileInfo fileInfo,
									SpriteAlignment align, IPsdConstructor constructor)
		{
			// Run the export on non exported layers
			PSDExporter.Export(settings, fileInfo, false);

			// Find all the layers being exported
			var exportLayers = PSDExporter.GetExportLayers(settings, fileInfo);

			// Stores the root object for each encountered group
			Dictionary<PSDLayerGroupInfo, GameObject> groupHeaders = new Dictionary<PSDLayerGroupInfo, GameObject>();

			// Store the last parent, for traversal
			GameObject lastParent = root;

			GameObject rootBase = null;
			Transform rootBaseT = null;

			int groupVisibleMask = 1;
			int groupDepth = 0;

			// Loop through all the layers of the PSD file
			// backwards so they appear in the expected order
			// Going through all the layers, and not just the exported layers
			// so that the groups can be setup
			for (int i = group.end; i >= group.start; i--)
			{
				// Skip if layer is hidden
				if (fileInfo.LayerVisibility[i] == false)
					continue;

				var groupInfo = fileInfo.GetGroupByLayerIndex(i);
				bool inGroup = groupInfo != null;

				// Skip if layer belongs to a hidden group
				if (inGroup && groupInfo.visible == false)
					continue;

				// When inside a group...
				if (inGroup)
				{
					// Inverted because starting backwards
					bool startGroup = groupInfo.end == i;
					bool closeGroup = groupInfo.start == i;

					// Go up or down group depths
					if (startGroup)
					{
						groupDepth++;
						groupVisibleMask |= ((groupInfo.visible ? 1 : 0) << groupDepth);
					}
					if (closeGroup)
					{
						// Reset group visible flag when closing group
						groupVisibleMask &= ~(1 << groupDepth);
						groupDepth--;
					}

					// First, check if parents of this group is visible in the first place
					bool parentVisible = true;
					for (int parentMask = groupDepth - 1; parentMask > 0; parentMask--)
					{
						bool isVisible = (groupVisibleMask & (1 << parentMask)) > 0;
						parentVisible &= isVisible;
					}
					// Parents not visible, continue to next layer
					if (!parentVisible)
						continue;

					// Finally, check if layer being processed is start/end of group
					if (startGroup || closeGroup)
					{
						// If start or end of the group, call HandleGroupObject
						// which creates the group layer object and assignment of lastParent
						HandleGroupObject(groupInfo, groupHeaders,
										startGroup, constructor, ref lastParent);

						// A bunch of book keeping needs to be done at the start of a group
						if (startGroup)
						{
							// If this is the start of the group being constructed
							// store as the rootBase
							if (i == group.end)
							{
								rootBase = lastParent;
								rootBaseT = rootBase.transform;
							}
						}

						// Start or end group doesn't have visible sprite object, skip to next layer
						continue;
					}
				} // End processing of group start/end

				// If got to here, processing a visual layer

				// Skip if the export layers list doesn't contain this index
				if (exportLayers.Contains(i) == false)
					continue;

				// If got here and root base hasn't been set, that's a problem
				if (rootBase == null)
				{
					throw new Exception("Trying to create image layer before root base has been set");
				}

				// Get layer info
				Layer layer = settings.Psd.Layers[i];

				// Create the game object for the sprite
				GameObject spriteObject = constructor.CreateGameObject(layer.Name, lastParent);

				// Reparent created object to last parent
				if (lastParent != null)
					spriteObject.transform.SetParent(lastParent.transform, false);

				// Retrieve sprite from asset database
				string sprPath = PSDExporter.GetLayerFilename(settings, i);
				Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(sprPath);

				// Get the pivot settings for the sprite
				TextureImporter sprImporter = (TextureImporter)AssetImporter.GetAtPath(sprPath);
				TextureImporterSettings sprSettings = new TextureImporterSettings();
				sprImporter.ReadTextureSettings(sprSettings);
				sprImporter = null;

				// Add components to the sprite object for the visuals
				constructor.AddComponents(i, spriteObject, sprite, sprSettings);

				Transform spriteT = spriteObject.transform;

				// Reposition the sprite object according to PSD position
				Vector2 spritePivot = GetPivot(sprSettings);

				Vector3 layerPos = constructor.GetLayerPosition(layer.Rect, spritePivot, settings.PixelsToUnitSize);

				// Scaling factor, if sprites were scaled down
				float posScale = 1f;
				switch (settings.ScaleBy)
				{
					case 1:
						posScale = 0.5f;
						break;
					case 2:
						posScale = 0.25f;
						break;
				}
				layerPos *= posScale;

				// Sprite position is based on root object position initially
				spriteT.position = (rootBaseT.position + layerPos);
			} // End layer loop

			// Loop through the groups and reposition according to alignment
			var groups = groupHeaders.Values.ToArray();
			for (int grpIndex = 0; grpIndex < groups.Length; grpIndex++)
			{
				var groupObject = groups[grpIndex];
				if (groupObject == null)
					continue;

				Transform groupT = groupObject.transform;

				// Store the sibling index, order important for UI
				// reconstruction that was established earlier
				int siblingIndex = groupT.GetSiblingIndex();

				// Get the position from the root pos function
				Vector3 groupPos = constructor.GetGroupPosition(groupObject, align);

				// Create a new object
				GameObject newRoot = constructor.CreateGameObject(groupObject.name, groupObject);

				// Reparent new object to same parent as old group object
				Transform newRootT = newRoot.transform;
				newRootT.SetParent(groupT.parent);

				// Take over the sibling index of the old group object
				newRootT.SetSiblingIndex(siblingIndex);

				// Reposition the new object
				newRootT.position = groupPos;

				// Reparent the children from the old root object to new root
				while (groupT.childCount > 0)
				{
					groupT.GetChild(0).SetParent(newRootT, true);
				}

				// If the group we're handling is rootBaseT, position the
				// replacement group header over old root
				if (groupT == rootBaseT)
				{
					newRootT.position = rootBaseT.position;
				}

				// Destroy the old root
				Object.DestroyImmediate(groups[grpIndex]);
			} // End reposition loop

		} // End BuildPsd()

		private static void HandleGroupObject(PSDLayerGroupInfo groupInfo,
									Dictionary<PSDLayerGroupInfo, GameObject> groupHeaders,
									bool startGroup, IPsdConstructor constructor,
									ref GameObject lastParent)
		{
			if (startGroup)
			{
				GameObject groupRoot = constructor.CreateGameObject(groupInfo.name, lastParent);
				constructor.HandleGroupOpen(groupRoot);

				lastParent = groupRoot;
				groupHeaders.Add(groupInfo, groupRoot);
				return;
			}

			// If not startGroup, closing group
			var header = groupHeaders[groupInfo].transform;
			if (header.parent != null)
			{
				constructor.HandleGroupClose(lastParent);

				lastParent = groupHeaders[groupInfo].transform.parent.gameObject;
			}
			else
			{
				lastParent = null;
			}
		}
		#endregion

		#region Public APIs

		public static Vector3 CalculateLayerPosition(Rect layerSize, Vector2 layerPivot)
		{
			Vector3 layerPos = Vector3.zero;
			layerPos.x = ((layerSize.width * layerPivot.x) + layerSize.x);
			layerPos.y = (-(layerSize.height * (1 - layerPivot.y)) - layerSize.y);
			return layerPos;
		}

		public static Vector2 GetPivot(SpriteAlignment spriteAlignment)
		{
			Vector2 pivot = new Vector2(0.5f, 0.5f);
			if (spriteAlignment == SpriteAlignment.TopLeft ||
				spriteAlignment == SpriteAlignment.LeftCenter ||
				spriteAlignment == SpriteAlignment.BottomLeft)
			{
				pivot.x = 0f;
			}
			if (spriteAlignment == SpriteAlignment.TopRight ||
				spriteAlignment == SpriteAlignment.RightCenter ||
				spriteAlignment == SpriteAlignment.BottomRight)
			{
				pivot.x = 1;
			}
			if (spriteAlignment == SpriteAlignment.TopLeft ||
				spriteAlignment == SpriteAlignment.TopCenter ||
				spriteAlignment == SpriteAlignment.TopRight)
			{
				pivot.y = 1;
			}
			if (spriteAlignment == SpriteAlignment.BottomLeft ||
				spriteAlignment == SpriteAlignment.BottomCenter ||
				spriteAlignment == SpriteAlignment.BottomRight)
			{
				pivot.y = 0;
			}
			return pivot;
		}

		public static Vector2 GetPivot(TextureImporterSettings sprSettings)
		{
			SpriteAlignment align = (SpriteAlignment) sprSettings.spriteAlignment;
			if (align == SpriteAlignment.Custom)
				return sprSettings.spritePivot;
			return GetPivot(align);
		}
		#endregion
	}
}