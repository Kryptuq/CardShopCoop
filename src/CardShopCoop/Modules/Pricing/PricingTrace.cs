using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using CardShopCoop.Api;
using HarmonyLib;

namespace CardShopCoop.Modules.Pricing
{
    /// <summary>DIAGNOSTIC ONLY (branch diag/pricing-item-trace, never release). Traces every
    /// price write and every price-tag text for EPL-minted products (id >= 200000) on both sides,
    /// so a host/client shelf-price split can be followed write by write. Every line starts with
    /// "[pricetrace]".</summary>
    internal static class PricingTrace
    {
        private const int StackBudgetPerSite = 15;
        private static readonly Dictionary<string, int> StackBudget = new();
        private static readonly Dictionary<int, float> LastTagMismatch = new();

        internal static bool Traced(EItemType type) => (int)type >= PricingInterop.EplMintFloor;

        internal static void Item(string site, EItemType type, string detail, bool withStack = false)
        {
            if (!Traced(type))
            {
                return;
            }

            var line = "[pricetrace] " + site + " item=" + (int)type + " " + detail;
            if (withStack && TakeStack(site))
            {
                line += " | caller: " + Caller(3);
            }

            CoopLog.Info(line);
        }

        internal static void Baseline(string site, IList<EItemType> types, IList<float> prices)
        {
            var sb = new StringBuilder("[pricetrace] " + site + " modded:");
            var count = 0;
            for (var i = 0; i < types.Count && i < prices.Count; i++)
            {
                if (!Traced(types[i]))
                {
                    continue;
                }

                sb.Append(' ').Append((int)types[i]).Append('=').Append(F(prices[i]));
                count++;
            }

            sb.Append(" (count=").Append(count).Append(')');
            CoopLog.Info(sb.ToString());
        }

        internal static string F(float value) => float.IsNaN(value) ? "NaN" : value.ToString("0.00");

        /// <summary>Postfix on UI_PriceTag.SetPriceText: reports a modded item tag whose text is
        /// not the value the co-op layer holds (mirror) or not the local price store, and who set
        /// it. This is the line that shows a write path the pricing sync never sees.</summary>
        internal static void TagText(UI_PriceTag tag, float shown)
        {
            try
            {
                if (tag == null || tag.GetCardData() != null)
                {
                    return;
                }

                var type = PricingInterop.Canonical(tag.GetItemType());
                if (!Traced(type))
                {
                    return;
                }

                var mirror = PricingDisplay.ReadItem(type);
                var store = PricingInterop.ReadItem(type);
                if (Math.Abs(shown - mirror) <= PricingInterop.PriceEpsilon
                    && Math.Abs(shown - store) <= PricingInterop.PriceEpsilon)
                {
                    return;
                }

                // One line per (item, shown value): a stuck tag must not flood the log.
                if (LastTagMismatch.TryGetValue((int)type, out var last)
                    && Math.Abs(last - shown) <= PricingInterop.PriceEpsilon)
                {
                    return;
                }

                LastTagMismatch[(int)type] = shown;
                CoopLog.Info("[pricetrace] tag-mismatch item=" + (int)type + " shown=" + F(shown)
                    + " mirror=" + F(mirror) + " store=" + F(store) + " | caller: " + Caller(3));
            }
            catch (Exception e)
            {
                CoopLog.Swallow(e);
            }
        }

        private static bool TakeStack(string site)
        {
            StackBudget.TryGetValue(site, out var used);
            if (used >= StackBudgetPerSite)
            {
                return false;
            }

            StackBudget[site] = used + 1;
            return true;
        }

        private static string Caller(int skip)
        {
            try
            {
                var frames = new StackTrace(skip, false).GetFrames();
                if (frames == null)
                {
                    return "?";
                }

                var sb = new StringBuilder();
                var shown = 0;
                for (var i = 0; i < frames.Length && shown < 8; i++)
                {
                    var method = frames[i].GetMethod();
                    if (method == null)
                    {
                        continue;
                    }

                    var owner = method.DeclaringType?.FullName ?? "?";
                    if (owner.StartsWith("CardShopCoop.Modules.Pricing.PricingTrace", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (shown > 0)
                    {
                        sb.Append(" <- ");
                    }

                    sb.Append(owner).Append('.').Append(method.Name);
                    shown++;
                }

                return sb.ToString();
            }
            catch (Exception e)
            {
                CoopLog.Swallow(e);
                return "?";
            }
        }
    }

    internal static class PricingTracePatches
    {
        internal static void Apply(Harmony harmony)
        {
            try
            {
                harmony.CreateClassProcessor(typeof(PriceTagText)).Patch();
            }
            catch (Exception e)
            {
                CoopLog.Warn("[pricetrace] tag text trace unavailable: " + e.Message);
            }
        }

        [HarmonyPatch(typeof(UI_PriceTag), "SetPriceText", new[] { typeof(float) })]
        private static class PriceTagText
        {
            [HarmonyPostfix]
            private static void Postfix(UI_PriceTag __instance, float __0)
                => PricingTrace.TagText(__instance, __0);
        }
    }
}
