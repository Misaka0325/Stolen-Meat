using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace StolenMeat
{
    /// <summary>Displays one cell using native window and tooltip components.</summary>
    internal static class AnimalInspectionWindow
    {
        private const string WindowId = "stolen_meat.animal_inspection";
        private static AnimalCellIndex cell;
        private static AnimalInspectionNavigation navigation;
        private static GameItem source;
        private static CustomUIWindow window;

        internal static bool Open(GameItem item)
        {
            if (!AnimalCellIndex.TryCreate(item, out var snapshot)) return false;
            CustomUIManager.Instance.CloseWindow(WindowId);
            cell = snapshot;
            source = item;
            navigation = new AnimalInspectionNavigation(snapshot.Records, item.unitCount == 1);
            var builder = CustomUIManager.Window(WindowId, "\u68c0\u89c6\u9f20\u7fa4", CustomUIManager.LAYER_OVERLAY)
                .SetSize(520, 500).Center()
                .OnClose(() => { cell = null; source = null; navigation = null; window = null; });
            builder.BeginRow(8, TextAnchor.MiddleCenter)
                .AddButton("", () => Switch(AnimalInspectionNavigation.ViewMode.Piles), id: "piles")
                .AddButton("", () => Switch(AnimalInspectionNavigation.ViewMode.Individuals), id: "individuals")
                .End();
            builder.AddLabel("", id: "total")
                .BeginScroll(330).AddLabel("", id: "details").End();
            builder.BeginRow(8, TextAnchor.MiddleCenter)
                .AddButton("\u4e0a\u4e00\u4e2a", () => Move(false), id: "previous")
                .AddFlexibleSpace()
                .AddButton("\u4e0b\u4e00\u4e2a", () => Move(true), id: "next")
                .End();
            window = builder.Show();
            window.BringToFront();
            Refresh();
            return true;
        }

        private static bool Current()
        {
            if (cell != null && cell.IsCurrent && navigation != null && source != null) return true;
            CustomUIManager.Instance.CloseWindow(WindowId);
            return false;
        }

        private static void Switch(AnimalInspectionNavigation.ViewMode mode)
        {
            if (!Current()) return;
            navigation.Select(mode);
            Refresh();
        }

        private static void Move(bool next)
        {
            if (!Current()) return;
            if (next) navigation.Next(); else navigation.Previous();
            Refresh();
        }

        private static void Refresh()
        {
            if (window == null || !Current()) return;
            window.Get("piles").SetText("\u9f20\u5806\uff08" + navigation.PileCount + "\uff09");
            window.Get("individuals").SetText("\u5355\u53ea\uff08" + navigation.IndividualCount + "\uff09");
            window.Get("total").SetText("\u672c\u683c\u8001\u9f20\u603b\u6570\uff1a" + cell.TotalAnimals);
            window.Get("previous").SetVisible(navigation.CanPrevious);
            window.Get("next").SetVisible(navigation.CanNext);
            string details = navigation.Mode == AnimalInspectionNavigation.ViewMode.Piles
                ? DescribePile(navigation.CurrentPile) : DescribeIndividual(navigation.CurrentIndividual);
            window.Get("details").SetText(details);
        }

        private static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

        private static string DescribePile(IReadOnlyList<AnimalStackState.AnimalRecord> records)
        {
            if (records == null) return "\u6682\u65e0\u9f20\u5806";
            var list = new List<AnimalStackState.AnimalRecord>(records);
            long count = AnimalStackState.Count(list);
            double hunger = 0d, prion = 0d, disease = 0d, food = 0d, growth = 0d;
            foreach (var item in list)
            {
                hunger += (item.HasHungerDistribution || AnimalStackState.IsHungryCohort(item)
                    ? item.TotalHungerRate : 0d) * item.Quantity;
                prion += AnimalStackState.PrionProbability(item) * item.Quantity;
                disease += item.DiseaseRate * item.Quantity;
                food += item.FoodShare * item.Quantity;
                growth += item.VirtualGrowth * item.Quantity;
            }
            string value(string label, double v) => label + "\uff1a" + Format(v) + "\n";
            string percent(string label, double v) => label + "\uff1a" + Format(v) + "%\n";
            string text = value("\u9f20\u5806\u6570\u91cf", count)
                + value("\u5e73\u5747\u5e74\u9f84", AnimalStackHooks.Average(list, "ANIMAL_AGE_TAG"))
                + value("\u5e73\u5747\u4f53\u91cd", AnimalStackHooks.Average(list, "ANIMAL_WEIGHT_TAG"))
                + value("\u5e73\u5747\u9971\u98df", AnimalStackHooks.Average(list, "ANIMAL_HUNGER_TAG"))
                + value("\u751f\u547d\u503c", AnimalStackHooks.Average(list, "ANIMAL_HEALTH_TAG"))
                + value("\u57fa\u7840\u751f\u547d\u4e0a\u9650", AnimalStackHooks.Average(list, "ANIMAL_MAX_HEALTH_TAG"))
                + value("\u57fa\u7840\u514d\u75ab\u503c", AnimalStackHooks.Average(list, "ANIMAL_IMMUNITY_TAG"))
                + value("\u57fa\u7840\u5bff\u547d", AnimalStackHooks.Average(list, "ANIMAL_LONGEVITY_TAG"))
                + value("\u57fa\u7840\u6bcf\u80ce\u6570\u91cf", AnimalStackHooks.Average(list, "ANIMAL_LITTER_SIZE_TAG"))
                + value("\u57fa\u7840\u9965\u997f\u6d88\u8017", AnimalStackHooks.Average(list, "ANIMAL_HUNGER_RATE_TAG"))
                + value("\u57fa\u7840\u7e41\u6b96\u529b", AnimalStackHooks.Average(list, "ANIMAL_FERTILITY_TAG"))
                + value("\u57fa\u7840\u751f\u957f\u7387", AnimalStackHooks.Average(list, "ANIMAL_GROWTH_RATE_TAG"))
                + value("\u865a\u62df\u751f\u80b2\u7387", AnimalFertilityCoordinator.AverageForDisplay(list))
                + value("\u5e73\u5747\u865a\u62df\u6210\u957f", growth / count)
                + value("\u5e73\u5747\u5df2\u5206\u914d\u98df\u7269", food / count)
                + percent("\u603b\u9965\u997f\u7387", 100d * hunger / count)
                + percent("\u751f\u75c5\u7387", 100d * disease / count)
                + percent("\u6718\u75c5\u6bd2\u7387", 100d * prion / count);
            long females = 0, males = 0, pregnant = 0, maleNeutered = 0, femaleNeutered = 0;
            bool countsKnown = true;
            foreach (var item in list)
            {
                string sex = item.Tag("ANIMAL_SEXE_TAG")?.GetString();
                if (item.Choices.ContainsKey("S:ANIMAL_SEXE_TAG")
                    || item.Variances.ContainsKey("P:ANIMAL_NEUTERED_TAG")
                    || item.Variances.ContainsKey("P:ANIMAL_PREGNANT_TAG")
                    || sex != "MALE" && sex != "FEMALE") { countsKnown = false; break; }
                bool neutered = item.Tag("ANIMAL_NEUTERED_TAG")?.valueEnabled == true;
                bool expecting = item.Tag("ANIMAL_PREGNANT_TAG")?.valueEnabled == true;
                if (sex == "FEMALE")
                {
                    females += item.Quantity;
                    if (neutered) femaleNeutered += item.Quantity;
                    if (expecting) pregnant += item.Quantity;
                }
                else
                {
                    males += item.Quantity;
                    if (neutered) maleNeutered += item.Quantity;
                }
            }
            if (countsKnown)
                text += value("\u6bcd\u9f20\u6570\u91cf", females) + value("\u516c\u9f20\u6570\u91cf", males)
                    + value("\u6000\u5b55\u6570\u91cf", pregnant)
                    + value("\u6bcd\u9f20\u7edd\u80b2\u6570\u91cf", femaleNeutered)
                    + value("\u516c\u9f20\u7edd\u80b2\u6570\u91cf", maleNeutered);
            else text += "\u6027\u522b\u3001\u6000\u5b55\u548c\u7edd\u80b2\u6570\u91cf\uff1a\u672a\u8bb0\u5f55\n";
            return text;
        }

        private static string DescribeIndividual(AnimalStackState.AnimalRecord record)
        {
            if (record == null) return "\u6682\u65e0\u5355\u53ea\u8001\u9f20";
            GameItem proxy = AnimalStackHooks.CreateProxy(source, record);
            try
            {
                proxy.parentInventory = source.parentInventory;
                var builder = new RichTextBuilder();
                HusbandryHelper.CreateItemTooltip(builder, proxy);
                return builder.Build().nakedText;
            }
            finally { proxy.parentInventory = null; proxy.Destroy(); }
        }
    }
}