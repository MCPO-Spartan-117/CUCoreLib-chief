using System;
using System.Collections.Generic;
using System.Linq;
using CUCoreLib.Registries;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CUCoreLib.Helpers
{
    internal sealed class SettingsMenuCategoryExtender : MonoBehaviour
    {
        private const int BuiltInTabCount = 5;
        private const float MinimumInterButtonGap = 2f;
        private const float TabRowGap = 4f;
        private const string CustomTabNamePrefix = "CUCoreLibSettingsTab_";
        private const float ScrollPixelsPerWheelStep = 48f;
        private readonly Dictionary<Button, int> buttonCategoryIndices = new Dictionary<Button, int>();

        private readonly List<Button> customButtons = new List<Button>();
        private readonly List<TMP_Dropdown> cachedDropdowns = new List<TMP_Dropdown>();
        private readonly List<Vector2> builtInAnchoredPositions = new List<Vector2>();
        private readonly List<Vector2> builtInSizes = new List<Vector2>();
        private int activeCategoryIndex;
        private string activeOwnedCategoryKey;
        private bool capturedBuiltInLayout;
        private SettingsMenu menu;

        private void Update()
        {
            if (!menu || !menu.content) return;

            if (IsMouseOverExpandedDropdown()) return;

            var maxScroll = GetMaxScroll();
            if (maxScroll <= 0f)
            {
                ClampScrollPosition();
                return;
            }

            var viewport = menu.content.parent as RectTransform;
            if (!viewport ||
                !RectTransformUtility.RectangleContainsScreenPoint(viewport, Input.mousePosition)) return;

            var scroll = Input.mouseScrollDelta.y;
            if (Mathf.Abs(scroll) < 0.01f) return;

            var anchoredPosition = menu.content.anchoredPosition;
            anchoredPosition.y = Mathf.Clamp(anchoredPosition.y - scroll * ScrollPixelsPerWheelStep, 0f, maxScroll);
            menu.content.anchoredPosition = anchoredPosition;
        }

        private bool IsMouseOverExpandedDropdown()
        {
            var mousePos = Input.mousePosition;
            return (from dd in cachedDropdowns
                where dd && dd.IsExpanded && dd.template
                select dd.template).Any(templateRect =>
                templateRect && templateRect.gameObject.activeInHierarchy &&
                RectTransformUtility.RectangleContainsScreenPoint(templateRect, mousePos));
        }

        // fixes dropdown templates created by the game's SettingsMenu.
        // vanilla prefab has a cramped viewport (only ~4 items visible)
        internal void FixDropdownsInContent(Transform content)
        {
            cachedDropdowns.Clear();
            if (!content) return;

            foreach (var dropdown in content.GetComponentsInChildren<TMP_Dropdown>(true))
            {
                if (!dropdown) continue;
                cachedDropdowns.Add(dropdown);
                FixDropdown(dropdown);
            }
        }

        // the overlay is too strange, i don't get it lol
        private static void FixDropdown(TMP_Dropdown dropdown)
        {
            var template = dropdown.template;
            if (!template) return;

            var templateCanvas = template.GetComponent<Canvas>();
            if (templateCanvas)
                templateCanvas.overrideSorting = true;

            var scrollRect = template.GetComponent<ScrollRect>();
            if (!scrollRect)
                scrollRect = template.gameObject.AddComponent<ScrollRect>();

            var viewport = template.Find("Viewport");
            if (viewport)
            {
                scrollRect.viewport = viewport as RectTransform;
                var viewportRect = viewport as RectTransform;
                if (viewportRect)
                    viewportRect.sizeDelta = new Vector2(viewportRect.sizeDelta.x, 200f);

                var content = viewport.Find("Content");
                if (content)
                    scrollRect.content = content as RectTransform;
            }

            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.scrollSensitivity = 24f;
        }

        internal static void EnsureAttached(SettingsMenu menu)
        {
            if (!menu) return;

            var helper = menu.GetComponent<SettingsMenuCategoryExtender>();
            if (!helper) helper = menu.gameObject.AddComponent<SettingsMenuCategoryExtender>();

            helper.Initialize(menu);
        }

        internal static void RefreshLiveMenu()
        {
            if (!SettingsMenu.instance) return;

            EnsureAttached(SettingsMenu.instance);
            var helper = SettingsMenu.instance.GetComponent<SettingsMenuCategoryExtender>();
            helper?.RefreshVisibleTab();
        }

        internal void Initialize(SettingsMenu settingsMenu)
        {
            menu = settingsMenu;
            if (menu == null) return;

            if (menu.buttons == null) menu.buttons = new List<Button>();

            activeCategoryIndex = Mathf.Clamp(activeCategoryIndex, 0, int.MaxValue);
            CaptureBuiltInLayoutIfNeeded();
            RegisterBuiltInButtons();
            RebuildButtons();
            ApplyButtonSprites();
            ClampScrollPosition();
        }

        internal void OnTabSelected(Setting.SettingCategory category)
        {
            activeCategoryIndex = (int)category;
            if (ModOptionsRegistry.TryGetOwnedCustomCategory(category, out var entry) && entry != null)
                activeOwnedCategoryKey = ModOptionsRegistry.NormalizeCustomCategoryKey(entry.DisplayName);
            else
                activeOwnedCategoryKey = null;
            SnapContentToTop();
            ApplyButtonSprites();
            ClampScrollPosition();
        }

        internal void RefreshVisibleTab()
        {
            if (menu == null) return;

            RebuildButtons();
            if (!string.IsNullOrWhiteSpace(activeOwnedCategoryKey) &&
                ModOptionsRegistry.TryGetOwnedCustomCategory(activeOwnedCategoryKey, out var activeEntry) &&
                activeEntry != null)
                activeCategoryIndex = activeEntry.CategoryIndex;
            // Refreshes fire while a menu stays open. So we don't snap back to the top haha
            var scrollY = menu.content != null ? menu.content.anchoredPosition.y : 0f;
            menu.SelectTab(activeCategoryIndex);
            if (menu.content != null)
            {
                var anchoredPosition = menu.content.anchoredPosition;
                anchoredPosition.y = scrollY;
                menu.content.anchoredPosition = anchoredPosition;
                ClampScrollPosition();
            }
        }

        private void RebuildButtons()
        {
            RemoveCustomButtons();
            PurgeUntrackedCustomButtons();
            buttonCategoryIndices.Clear();
            RestoreBuiltInLayout();
            RegisterBuiltInButtons();
            ModOptionsRegistry.ReconcileCustomCategoryOwnership(Settings.settings);

            var categories = ModOptionsRegistry.GetCustomCategories();
            if (menu == null || menu.buttons == null || menu.buttons.Count == 0)
            {
                return;
            }

            if (categories.Count == 0)
            {
                return;
            }

            var template = FindTemplateButton();
            if (!template) return;

            var templateRect = template.transform as RectTransform;
            if (templateRect == null) return;

            var parent = template.transform.parent;
            var origin = templateRect.anchoredPosition;

            for (var i = 0; i < categories.Count; i++)
            {
                var category = categories[i];
                var clone = Instantiate(template.gameObject, parent, false);
                clone.name = CustomTabNamePrefix + category.DisplayName;
                var cloneRect = clone.transform as RectTransform;
                if (cloneRect != null) cloneRect.anchoredPosition = origin;

                var button = clone.GetComponent<Button>();
                if (!button)
                {
                    Destroy(clone);
                    continue;
                }

                // The cloned prefab retains its inspector-wired callback. Remove it by replacing
                // the event, otherwise this tab selects both its template category and ours.
                button.onClick = new Button.ButtonClickedEvent();
                var categoryIndex = category.CategoryIndex;
                button.onClick.AddListener(delegate { menu.SelectTab(categoryIndex); });

                var label = clone.GetComponentInChildren<TextMeshProUGUI>(false)
                            ?? clone.GetComponentInChildren<TextMeshProUGUI>(true);
                if (label)
                {
                    label.text = category.DisplayName;
                    foreach (var localizer in label.GetComponents<MonoBehaviour>()
                                 .Where(component => component && component.GetType().Name.Contains("Local")))
                    {
                        Destroy(localizer);
                    }

                    NormalizeTabLabel(label);
                }

                menu.buttons.Add(button);
                customButtons.Add(button);
                buttonCategoryIndices[button] = categoryIndex;
            }

            ReflowButtonsIntoOriginalBand();
        }

        private void RemoveCustomButtons()
        {
            if (menu != null && menu.buttons != null)
                foreach (var button in customButtons)
                    menu.buttons.Remove(button);

            foreach (var button in customButtons)
                if (button)
                {
                    buttonCategoryIndices.Remove(button);
                    Destroy(button.gameObject);
                }

            customButtons.Clear();
        }

        private void PurgeUntrackedCustomButtons()
        {
            var parent = FindStripParent();
            if (!parent) return;

            for (var i = parent.childCount - 1; i >= 0; i--)
            {
                var child = parent.GetChild(i);
                if (!child || !child.name.StartsWith(CustomTabNamePrefix, StringComparison.Ordinal)) continue;
                if (customButtons.Any(button => button && button.gameObject == child.gameObject)) continue;

                Destroy(child.gameObject);
            }
        }

        private Transform FindStripParent()
        {
            var first = menu?.buttons?.FirstOrDefault(button => button != null);
            return first != null ? first.transform.parent : null;
        }

        private void RegisterBuiltInButtons()
        {
            var builtInCount = Mathf.Min(BuiltInTabCount, menu.buttons.Count);
            for (var i = 0; i < builtInCount; i++)
            {
                var button = menu.buttons[i];
                if (button != null) buttonCategoryIndices[button] = i;
            }
        }

        private Button FindTemplateButton()
        {
            if (menu == null || menu.buttons == null || menu.buttons.Count == 0) return null;

            var builtInCount = Mathf.Min(BuiltInTabCount, menu.buttons.Count);
            for (var i = builtInCount - 1; i >= 0; i--)
            {
                var button = menu.buttons[i];
                if (button != null && !customButtons.Contains(button)) return button;
            }

            return menu.buttons.LastOrDefault(button => button != null && !customButtons.Contains(button));
        }

        private void ApplyButtonSprites()
        {
            if (menu == null || menu.buttons == null) return;

            foreach (var button in menu.buttons)
            {
                if (!button) continue;

                var image = button.GetComponent<Image>();
                if (image == null) continue;
                if (!buttonCategoryIndices.ContainsKey(button)) continue;
                var isActive = buttonCategoryIndices.TryGetValue(button, out var categoryIndex)
                               && categoryIndex == activeCategoryIndex;
                image.sprite = isActive ? menu.buttonOpen : menu.buttonClosed;
            }
        }

        private void SnapContentToTop()
        {
            if (menu?.content == null) return;

            var anchoredPosition = menu.content.anchoredPosition;
            anchoredPosition.y = 0f;
            menu.content.anchoredPosition = anchoredPosition;
        }

        private void ClampScrollPosition()
        {
            if (menu?.content == null) return;

            var anchoredPosition = menu.content.anchoredPosition;
            anchoredPosition.y = Mathf.Clamp(anchoredPosition.y, 0f, GetMaxScroll());
            menu.content.anchoredPosition = anchoredPosition;
        }

        private float GetMaxScroll()
        {
            if (menu?.content == null) return 0f;

            var viewport = menu.content.parent as RectTransform;
            if (viewport == null) return 0f;

            return Mathf.Max(0f, menu.content.sizeDelta.y - viewport.rect.height);
        }

        private void CaptureBuiltInLayoutIfNeeded()
        {
            if (capturedBuiltInLayout || menu == null || menu.buttons == null || menu.buttons.Count < BuiltInTabCount)
            {
                return;
            }

            builtInAnchoredPositions.Clear();
            builtInSizes.Clear();

            for (var i = 0; i < BuiltInTabCount; i++)
            {
                var rect = menu.buttons[i] != null ? menu.buttons[i].transform as RectTransform : null;
                if (rect == null)
                {
                    builtInAnchoredPositions.Clear();
                    builtInSizes.Clear();
                    return;
                }

                builtInAnchoredPositions.Add(rect.anchoredPosition);
                builtInSizes.Add(rect.sizeDelta);
            }

            capturedBuiltInLayout = builtInAnchoredPositions.Count == BuiltInTabCount;
        }

        private void RestoreBuiltInLayout()
        {
            if (!capturedBuiltInLayout || menu == null || menu.buttons == null) return;

            var builtInCount = Mathf.Min(Mathf.Min(BuiltInTabCount, menu.buttons.Count), builtInAnchoredPositions.Count);
            for (var i = 0; i < builtInCount; i++)
            {
                var rect = menu.buttons[i] != null ? menu.buttons[i].transform as RectTransform : null;
                if (rect == null) continue;

                rect.anchoredPosition = builtInAnchoredPositions[i];
                rect.sizeDelta = builtInSizes[i];
            }
        }

        private void ReflowButtonsIntoOriginalBand()
        {
            if (!capturedBuiltInLayout) return;

            var strip = CollectStripButtons();
            if (strip.Count == 0) return;

            var firstRect = strip[0].transform as RectTransform;
            if (firstRect == null) return;

            var parentRect = firstRect.parent as RectTransform;
            if (parentRect == null) return;

            var rowHeight = builtInSizes[0].y;
            var bandLeft = GetBandLeft(firstRect, parentRect);
            var bandWidth = GetBandWidth(bandLeft, parentRect);
            var gap = strip.Count > 1 ? MinimumInterButtonGap : 0f;
            var singleRowWidth = (bandWidth - gap * (strip.Count - 1)) / strip.Count;

            var builtInRowY = firstRect.anchoredPosition.y;

            var customCount = strip.Count(button => customButtons.Contains(button));
            var builtInCount = strip.Count - customCount;

            if (customCount == 0 || singleRowWidth >= builtInSizes[0].x)
            {
                LayoutTabRow(strip, parentRect, bandLeft, bandWidth, gap, builtInRowY, rowHeight);
                return;
            }

            // Too many tabs, in this case we toss it above 
            LayoutTabRow(strip.GetRange(0, builtInCount), parentRect, bandLeft, bandWidth, gap, builtInRowY, rowHeight);
            LayoutTabRow(strip.GetRange(builtInCount, customCount), parentRect, bandLeft, bandWidth, gap,
                builtInRowY + rowHeight + TabRowGap, rowHeight);
        }

        private List<Button> CollectStripButtons()
        {
            var strip = new List<Button>();
            var builtInCount = Mathf.Min(BuiltInTabCount, menu.buttons.Count);

            for (var i = 0; i < builtInCount; i++)
            {
                var button = menu.buttons[i];
                if (button != null && !customButtons.Contains(button)) strip.Add(button);
            }

            foreach (var button in customButtons)
            {
                if (button && button.transform is RectTransform) strip.Add(button);
            }

            return strip;
        }

        private void LayoutTabRow(List<Button> row, RectTransform parentRect, float bandLeft, float bandWidth,
            float gap, float rowY, float rowHeight)
        {
            if (row.Count == 0) return;

            var targetWidth = Mathf.Max(1f, (bandWidth - gap * (row.Count - 1)) / row.Count);
            var currentLeft = bandLeft;

            foreach (var button in row)
            {
                var rect = button != null ? button.transform as RectTransform : null;
                if (rect == null) continue;

                rect.sizeDelta = new Vector2(targetWidth, rowHeight);
                rect.anchoredPosition = new Vector2(
                    currentLeft + targetWidth * 0.5f - GetAnchorRefX(rect, parentRect), rowY);
                currentLeft += targetWidth + gap;

                var label = button.GetComponentInChildren<TextMeshProUGUI>(true);
                if (label) NormalizeTabLabel(label);
            }
        }

        private float GetBandLeft(RectTransform firstRect, RectTransform parentRect)
        {
            return GetAnchorRefX(firstRect, parentRect) + firstRect.anchoredPosition.x - builtInSizes[0].x * 0.5f;
        }

        private float GetBandWidth(float bandLeft, RectTransform parentRect)
        {
            // The vanilla tabs only cover part of their parent. Mirror the left inset onto the right
            // side so the strip always fills the visible band, whatever the tab count is.
            var leftInset = bandLeft - parentRect.rect.xMin;
            return Mathf.Max(1f, parentRect.rect.width - leftInset * 2f);
        }

        private static float GetAnchorRefX(RectTransform rect, RectTransform parentRect)
        {
            var anchorMid = (rect.anchorMin.x + rect.anchorMax.x) * 0.5f;
            return parentRect.rect.xMin + anchorMid * parentRect.rect.width;
        }

        private static void NormalizeTabLabel(TMP_Text label)
        {
            if (label == null) return;

            var labelRect = label.transform as RectTransform;
            if (labelRect != null)
            {
                labelRect.anchorMin = Vector2.zero;
                labelRect.anchorMax = Vector2.one;
                labelRect.pivot = new Vector2(0.5f, 0.5f);
                labelRect.offsetMin = Vector2.zero;
                labelRect.offsetMax = Vector2.zero;
                labelRect.anchoredPosition = Vector2.zero;
            }

            label.alignment = TextAlignmentOptions.Center;
            label.enableAutoSizing = false;
        }
    }
}
