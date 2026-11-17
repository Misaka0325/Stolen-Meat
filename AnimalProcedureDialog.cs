using System;
namespace StolenMeat
{
    /// <summary>Displays the requested animal selection for a pending procedure.</summary>
    internal static class AnimalProcedureDialog
    {
        private const string WindowId = "stolen_meat.animal_procedure";
        internal static void Open(Action<AnimalPopulationCounts.Target> onSelect)
        {
            if (onSelect == null) throw new ArgumentNullException(nameof(onSelect));
            void Choose(AnimalPopulationCounts.Target target)
            {
                CustomUIManager.Instance.CloseWindow(WindowId);
                onSelect(target);
            }
            CustomUIWindow window = CustomUIManager.Window(WindowId, "\u9009\u62e9\u64cd\u4f5c\u5bf9\u8c61", CustomUIManager.LAYER_OVERLAY)
                .SetSize(320, 260).Center()
                .AddButton("\u516c\u9f20", () => Choose(AnimalPopulationCounts.Target.Male), id: "male")
                .AddButton("\u6bcd\u9f20\uff08\u672a\u6000\u5b55\uff09", () => Choose(AnimalPopulationCounts.Target.Female), id: "female")
                .AddButton("\u6000\u5b55\u6bcd\u9f20", () => Choose(AnimalPopulationCounts.Target.PregnantFemale), id: "pregnant")
                .AddButton("\u968f\u4fbf\u4e00\u4e2a", () => Choose(AnimalPopulationCounts.Target.Any), id: "any")
                .Show();
            window.BringToFront();
        }
    }
}