using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace subjectnerdagreement.psdexport
{
	public class UiImgConstructor : IPsdConstructor
	{
		public string MenuName { get { return "Unity UI"; } }

		public bool CanBuild(GameObject hierarchySelection)
		{
			Canvas parentCanvas = hierarchySelection.GetComponentInParent<Canvas>();
			return parentCanvas != null;
		}

		public GameObject CreateGameObject(string name, GameObject parent)
		{
			GameObject go = SpriteConstructor.GOFactory(name, parent);
			// Unity UI objects need Rect Transforms,
			// add the component after creating the base object
			go.AddComponent<RectTransform>();
			return go;
		}

		public void AddComponents(int layerIndex, GameObject imageObject, Sprite sprite, TextureImporterSettings settings)
		{
			var uiImg = imageObject.AddComponent<Image>();

			uiImg.sprite = sprite;
			uiImg.SetNativeSize();
			uiImg.rectTransform.SetAsFirstSibling();

			Vector2 sprPivot = PsdBuilder.GetPivot(settings);
			uiImg.rectTransform.pivot = sprPivot;
		}

		public void HandleGroupOpen(GameObject groupParent) { }

		public void HandleGroupClose(GameObject groupParent)
		{
			// Because Unity UI ordering is dependent on
			// the hierarchy order, reposition the layer group
			// when it is closed
			groupParent.transform.SetAsFirstSibling();
		}

		public Vector3 GetLayerPosition(Rect layerSize, Vector2 layerPivot, float pixelsToUnitSize)
		{
			return PsdBuilder.CalculateLayerPosition(layerSize, layerPivot);
		}

		public Vector3 GetGroupPosition(GameObject groupRoot, SpriteAlignment alignment)
		{

			Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
			Vector2 max = new Vector2(float.MinValue, float.MinValue);

			var tList = groupRoot.GetComponentsInChildren<RectTransform>();
			foreach (var rectTransform in tList)
			{
				if (rectTransform.gameObject == groupRoot)
					continue;

				var rectSize = rectTransform.sizeDelta;
				var rectPivot = rectTransform.pivot;

				var calcMin = rectTransform.position;
				calcMin.x -= rectSize.x * rectPivot.x;
				calcMin.y -= rectSize.y * rectPivot.y;

				var calcMax = calcMin + new Vector3(rectSize.x, rectSize.y);

				min = Vector2.Min(min, calcMin);
				max = Vector2.Max(max, calcMax);
			}

			Vector2 pivot = PsdBuilder.GetPivot(alignment);
			Vector3 pos = Vector3.zero;
			pos.x = Mathf.Lerp(min.x, max.x, pivot.x);
			pos.y = Mathf.Lerp(min.y, max.y, pivot.y);
			return pos;
		}
	}
}