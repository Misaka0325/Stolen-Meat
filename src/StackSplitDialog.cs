using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace StolenMeat
{
    /// <summary>Adds one split command to the game's existing item context menu.</summary>
    internal static class SplitContextMenu
    {
        private static readonly ConditionalWeakTable<ItemContextHandler, RichTextElement> Buttons =
            new ConditionalWeakTable<ItemContextHandler, RichTextElement>();

        private static readonly ConditionalWeakTable<ItemContextHandler, RichTextElement> InspectionButtons =
            new ConditionalWeakTable<ItemContextHandler, RichTextElement>();
        internal static bool CanInspect(GameItem item) => ItemStackPolicy.IsAnimal(item)
            && item.unitCount > 0 && AnimalStackState.IsValid(item);

        internal static bool CanSplit(GameItem item) => ItemStackPolicy.IsSupported(item)
            && item.unitCount > 1 && (!ItemStackPolicy.IsAnimal(item) || AnimalStackState.IsValid(item));

        /// <summary>Adds split and animal inspection to the native context grid for 拆分.</summary>
        internal static void Attach(ItemContextHandler handler, PixelWindow window,
            RenderHandler.ColorPalette disabledColor, params RichTextElement[] nativeButtons)
        {
            if (window?.childElement is not GridPixelElement original || nativeButtons.Length < 7) return;
            var expanded = new GridPixelElement(1, nativeButtons.Length + 2, false);
            var split = new RichTextElement(false, -1, -1, true);
            split.SetText("拆分", 10000, disabledColor);
            split.handler.gameObject.name = "nodeButtonSplit";
            split.handler.gameObject.tag = "ContextMenuNode";
            var inspect = new RichTextElement(false, -1, -1, true);
            inspect.SetText("\u68c0\u89c6\u9f20\u7fa4", 10000, disabledColor);
            inspect.handler.gameObject.name = "nodeButtonInspectAnimals";
            inspect.handler.gameObject.tag = "ContextMenuNode";
            try
            {
                foreach (RichTextElement button in nativeButtons)
                {
                    if (button == null || !original.Detach(button) || !expanded.Attach(button))
                        throw new InvalidOperationException("native context button could not be moved");
                }
                if (!expanded.Attach(split) || !expanded.Attach(inspect) || !window.Detach(original) || !window.Attach(expanded))
                    throw new InvalidOperationException("split command could not be attached");
                expanded.Validate();
                window.Validate();
                // Native menu hit testing requires this tag on the text render children too.
                foreach (UnityEngine.Transform child in window.handler.GetComponentsInChildren<UnityEngine.Transform>(true))
                    child.gameObject.tag = "ContextMenuNode";
                Buttons.Remove(handler);
                Buttons.Add(handler, split);
                InspectionButtons.Remove(handler);
                InspectionButtons.Add(handler, inspect);
                UnityEngine.Object.Destroy(original.handler.gameObject);
            }
            catch (Exception error)
            {
                // Leave the original menu usable if a game update changes its layout.
                if (ReferenceEquals(window.childElement, expanded)) window.Detach(expanded);
                foreach (RichTextElement button in nativeButtons)
                {
                    expanded.Detach(button);
                    if (button != null) original.Attach(button);
                }
                if (window.childElement == null) window.Attach(original);
                RatButcherPatch.Observer?.RecordSplit("native split menu unavailable: " + error);
            }
        }

        internal static bool Owns(ItemContextHandler handler, RichTextElement button) =>
            handler != null && button != null && Buttons.TryGetValue(handler, out RichTextElement split)
            && ReferenceEquals(split, button);

        internal static bool OwnsInspection(ItemContextHandler handler, RichTextElement button) =>
            handler != null && button != null && InspectionButtons.TryGetValue(handler, out RichTextElement inspect)
            && ReferenceEquals(inspect, button);

        /// <summary>Uses the same hover and disabled colors as native commands.</summary>
        internal static void UpdateColor(ItemContextHandler handler, RichTextElement selected, GameItem current,
            RenderHandler.ColorPalette enabled, RenderHandler.ColorPalette selectedEnabled,
            RenderHandler.ColorPalette disabled, RenderHandler.ColorPalette selectedDisabled)
        {
            if (!Buttons.TryGetValue(handler, out RichTextElement split)) return;
            bool hovered = ReferenceEquals(split, selected);
            split.SetText("拆分", 10000, CanSplit(current)
                ? (hovered ? selectedEnabled : enabled)
                : (hovered ? selectedDisabled : disabled));
            if (InspectionButtons.TryGetValue(handler, out var inspect))
            {
                bool inspectHovered = ReferenceEquals(inspect, selected);
                inspect.SetText("\u68c0\u89c6\u9f20\u7fa4", 10000, CanInspect(current)
                    ? (inspectHovered ? selectedEnabled : enabled)
                    : (inspectHovered ? selectedDisabled : disabled));
            }
        }

        internal static void MatchWidth(ItemContextHandler handler, RichTextElement nativeButton)
        {
            if (Buttons.TryGetValue(handler, out RichTextElement split))
                split.SetFixedSize(nativeButton.widthPixels, -1);
            if (InspectionButtons.TryGetValue(handler, out var inspect))
                inspect.SetFixedSize(nativeButton.widthPixels, -1);
        }
    }

    [HarmonyPatch(typeof(ItemContextHandler), "OnEventInit")]
    internal static class SplitMenuInitPatch
    {
        private static void Postfix(ItemContextHandler __instance, bool __result, PixelWindow ___contextWindow, ref GridPixelElement ___contextButtons,
            RenderHandler.ColorPalette ___textDisabledColor, RichTextElement ___buttonOpen,
            RichTextElement ___buttonUse, RichTextElement ___buttonEquip,
            RichTextElement ___buttonActivate, RichTextElement ___buttonConfigure, RichTextElement ___buttonToggle,
            RichTextElement ___buttonUnload, RichTextElement ___buttonPin)
        {
            if (!__result) return;
            SplitContextMenu.Attach(__instance, ___contextWindow, ___textDisabledColor,
                ___buttonOpen, ___buttonUse, ___buttonEquip, ___buttonActivate,
                ___buttonConfigure, ___buttonToggle, ___buttonUnload, ___buttonPin);
            if (___contextWindow.childElement is GridPixelElement updated) ___contextButtons = updated;
        }
    }

    [HarmonyPatch(typeof(ItemContextHandler), "UpdateSelectButtonColor")]
    internal static class SplitMenuColorPatch
    {
        private static void Postfix(ItemContextHandler __instance, RichTextElement __0,
            GameItem ___currentItem, RenderHandler.ColorPalette ___textColor,
            RenderHandler.ColorPalette ___textSelectColor,
            RenderHandler.ColorPalette ___textDisabledColor,
            RenderHandler.ColorPalette ___textSelectDisabledColor)
        {
            SplitContextMenu.UpdateColor(__instance, __0, ___currentItem, ___textColor,
                ___textSelectColor, ___textDisabledColor, ___textSelectDisabledColor);
        }
    }

    [HarmonyPatch(typeof(ItemContextHandler), "EqualizeButtonWidths")]
    internal static class SplitMenuWidthPatch
    {
        private static void Postfix(ItemContextHandler __instance, RichTextElement ___buttonOpen) =>
            SplitContextMenu.MatchWidth(__instance, ___buttonOpen);
    }

    [HarmonyPatch(typeof(ItemContextHandler), "TriggerButton")]
    internal static class SplitMenuClickPatch
    {
        private static bool Prefix(ItemContextHandler __instance, RichTextElement __0,
            GameItem ___currentItem, PixelWindow ___contextWindow)
        {
            if (SplitContextMenu.OwnsInspection(__instance, __0))
            {
                if (SplitContextMenu.CanInspect(___currentItem) && AnimalInspectionWindow.Open(___currentItem))
                    ___contextWindow.Hide();
                return false;
            }
            if (!SplitContextMenu.Owns(__instance, __0)) return true;
            if (SplitContextMenu.CanSplit(___currentItem))
            {
                ___contextWindow.Hide();
                StackSplitDialog.Open(___currentItem);
            }
            return false;
        }
    }

    /// <summary>Validates and places a split stack before subtracting the source amount.</summary>
    internal static class StackSplitDialog
    {
        private const string WindowId = "stolen_meat.split";

        /// <summary>Shows a quantity prompt for the selected native stack.</summary>
        internal static void Open(GameItem source)
        {
            string input = "1";
            CustomUIManager.Window(WindowId, "拆分物品", CustomUIManager.LAYER_OVERLAY)
                .SetSize(320, 200)
                .AddLabel("当前数量：" + source.unitCount)
                .AddInput("拆分数量", "1", value => input = value, id: "quantity")
                .AddButton("拆分", () => TrySplit(source, input), id: "split")
                .Show();
        }

        /// <summary>Places a cloned stack before reducing the source count.</summary>
        private static void TrySplit(GameItem source, string input)
        {
            if (!ItemStackPolicy.IsSupported(source)) return;
            if (!int.TryParse(input, NumberStyles.None, CultureInfo.InvariantCulture, out int amount)
                || amount < 1 || amount > source.unitCount)
            {
                RatButcherPatch.Observer?.RecordSplit("rejected: quantity must be between 1 and " + source.unitCount);
                return;
            }
            GameItem newStack;
            try { newStack = NativeSplitPreparation.Create(source, amount); }
            catch (Exception error)
            {
                RatButcherPatch.Observer?.RecordSplit("rejected: native split preparation failed: " + error);
                return;
            }
            newStack.SetUnitCount(amount);
            if (ItemStackPolicy.IsAnimal(source) && !AnimalStackState.CopyTop(source, newStack, amount))
            {
                newStack.Destroy();
                RatButcherPatch.Observer?.RecordSplit("rejected: animal stack state is invalid");
                return;
            }
            SlotMarker slot = null;
            if (source.parentInventory != null)
            {
                ItemStackPolicy.SuppressStackingForSplit = true;
                try { slot = source.parentInventory.TryFindOneValidInventorySlot(newStack); }
                finally { ItemStackPolicy.SuppressStackingForSplit = false; }
            }
            int consumed = 0;
            int discarded = 0;
            // The game's slot search checks the whole item shape (including a 2x2 meat).
            if (slot != null && slot.itemGridShape != null && slot.targetItem == null
                && slot.numTransfer >= amount)
                consumed = slot.TryAcceptOnce(amount);
            if (consumed < amount)
            {
                OverflowCoordinator overflow = OverflowPatch.Coordinator;
                if (overflow != null && overflow.RouteSplit(newStack, out int routed, out discarded))
                    consumed += routed;
                else if (newStack.parentInventory == null) newStack.Destroy();
            }
            if (consumed == 0)
            {
                RatButcherPatch.Observer?.RecordSplit("rejected: no destination available; source unchanged");
                return;
            }
            int remaining = source.unitCount - consumed;
            if (ItemStackPolicy.IsAnimal(source)) AnimalStackState.ConsumeTop(source, consumed);
            else if (remaining == 0) source.Destroy();
            else source.SetUnitCount(remaining);
            RatButcherPatch.Observer?.RecordSplit("accepted: source=" + source.uniqueId + ", new=" + newStack.uniqueId
                + ", requested=" + amount + ", consumed=" + consumed
                + ", discarded=" + discarded + ", remaining=" + remaining);
            CustomUIManager.Instance.CloseWindow(WindowId);
        }
    }
}







