using System;
using System.Reflection;

namespace StolenMeat
{
    /// <summary>Runs native splitting on an isolated staging item before committing placement.</summary>
    internal static class NativeSplitPreparation
    {
        // These are the same runtime callbacks copied by the game's CloneLinked method.
        private static readonly string[] CopiedProperties = {
            "ability", "mayInventoryAddItemFunc", "mayInventoryRemoveItemFunc", "mayStackItemFunc",
            "maxSlotAddItemFunc", "maxSlotRemoveItemFunc", "maxSlotAmountItemFunc", "minSlotAmountItemFunc",
            "onSlotAddItemFunc", "onSlotRemoveItemFunc", "onSlotChangeAmountItemFunc", "onChangeAmountItemFunc",
            "slotPriorityFunc", "slotSearchPassthroughForItemFunc", "parentItemShapeModifierForItemFunc",
            "parentItemSpriteModifierForItemFunc", "parentItemStateModifierForItemFunc", "isItemStateValidFunc",
            "tooltipRichTextFunc", "maySelectSlotItemFunc", "canSelectSlotItemFunc", "mayItemTargetItemFunc",
            "canItemTargetItemFunc", "onItemTargetItemFunc", "maySlotItemTargetItemFunc", "canSlotItemTargetItemFunc",
            "onSlotItemTargetItemFunc", "mayItemTargetSlotItemFunc", "canItemTargetSlotItemFunc", "onItemTargetSlotItemFunc",
            "mayActivateSlotItemFunc", "canActivateSlotItemFunc", "onActivateSlotItemFunc", "mayToggleSlotItemFunc",
            "canToggleSlotItemFunc", "onToggleSlotItemFunc", "onConfigureSlotItemFunc", "onUnloadChildItemFunc",
            "soundActivate", "soundContainerOpen", "soundDragEnd", "soundDragStart", "soundExamineOpen",
            "soundSelect", "soundToggle", "soundUse"
        };

        private static void CopyRuntimeProperties(GameItem source, GameItem target)
        {
            foreach (string name in CopiedProperties)
            {
                PropertyInfo property = typeof(GameItem).GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                if (property != null && property.CanRead && property.CanWrite)
                    property.SetValue(target, property.GetValue(source));
            }
        }
        // Copies item state without performing a split or touching inventory UI.
        internal static GameItem CreateCopy(GameItem source, int amount)
        {
            Validate(source, amount);
            GameItem result = null;
            try
            {
                result = DirectoryMaster.Item(source.identifier, true);
                if (result == null) throw new InvalidOperationException("Item copy could not be created.");
                CopyItemState(source, result);
                result.SetUnitCount(amount);
                return result;
            }
            catch
            {
                if (result != null && result.parentInventory == null) result.Destroy();
                throw;
            }
        }

        private static void Validate(GameItem source, int amount)
        {
            if (source == null || amount < 1 || amount > source.unitCount
                || source.contentWindow != null || source.examineWindow != null)
                throw new ArgumentException("The item cannot be safely split.");
        }

        private static void CopyItemState(GameItem source, GameItem result)
        {
            // Restore the source state and appearance on the new item.
            result.state = source.state.Clone();
            result.modifiedState = source.modifiedState.Clone();
            result.shape = source.shape.Clone();
            result.modifiedShape = source.modifiedShape.Clone();
            result.spritePath = source.spritePath;
            result.spriteAtlasPath = source.spriteAtlasPath;
            // Native CloneLinked also marks the copied sprite for redraw.
            PropertyInfo spriteChanged = typeof(GameItem).GetProperty("spriteChanged",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (spriteChanged?.GetSetMethod(true) != null) spriteChanged.SetValue(result, true);
            result.activateDefault = source.activateDefault;
            result.shortDescription = source.shortDescription;
            result.longDescription = source.longDescription;
            result.unitValue = source.unitValue;
            result.unitBaseValue = source.unitBaseValue;
            result.lateUnitValue = source.lateUnitValue;
            result.backupUnitValue = source.backupUnitValue;
            result.name = source.name;
            result.customText = source.customText;
            CopyRuntimeProperties(source, result);
        }
        internal static GameItem Create(GameItem source, int amount)
        {
            Validate(source, amount);
            GameItem staging = null, result = null;
            GameItem previousStaging = AnimalNativeSplitPatch.PreparedSplitSource;
            try
            {
                staging = ItemDirectory.CreateEmptyItem(source.identifier);
                if (staging == null) throw new InvalidOperationException("Native staging item could not be created.");
                staging.SetUnitCount(source.unitCount);
                AnimalNativeSplitPatch.PreparedSplitSource = staging;
                result = staging.GetSplitItem(amount);
                if (result == null || result.unitCount != amount)
                    throw new InvalidOperationException("Native split returned an invalid item.");
                CopyItemState(source, result);
                return result;
            }
            catch
            {
                if (result != null && result.parentInventory == null) result.Destroy();
                throw;
            }
            finally
            {
                AnimalNativeSplitPatch.PreparedSplitSource = previousStaging;
                if (staging != null && staging.parentInventory == null) staging.Destroy();
            }
        }
    }
}