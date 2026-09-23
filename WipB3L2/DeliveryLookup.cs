namespace OrderTrackingWeb.WipB3L2;

public sealed record DeliveryLocation(string MoNumber, string ShelfCode, string Area, string MatchedBy);

public static class DeliveryLookup
{
    public static List<DeliveryLocation> Find(WipState state, string mw, IReadOnlyDictionary<string, string> areas)
    {
        static string Normalize(string? code) => (code ?? "").Trim().ToUpperInvariant();
        var target = Normalize(mw);
        if (target.Length == 0) return [];
        return state.Cards.SelectMany(card => card.MoNumbers.Select(mo => (card, mo)))
            .Select(item => {
                var code = Normalize(item.mo);
                var baseCode = System.Text.RegularExpressions.Regex.Replace(code, @"-XE[1-9]\d*$", "");
                if (code != target && baseCode != target) return null;
                var line = item.card.ShelfCode.Split('-')[0];
                var area = areas.TryGetValue(line, out var label) && !string.IsNullOrWhiteSpace(label)
                    ? label : "Chưa phân khu";
                return new DeliveryLocation(item.mo, item.card.ShelfCode, area, "MW");
            })
            .OfType<DeliveryLocation>().OrderBy(x => x.ShelfCode).ThenBy(x => x.MoNumber).ToList();
    }
}
