using System;
using System.Collections.Generic;
using PlanetJem.Core;

namespace NightRunnersMP;

// one model can have several list entries (other chassis, sunroof) and spawn by model always takes the last,
// sometimes an AI-only twin. so a car goes out as model + entry, packed in the int the server passes on unread.
// any car in the files shows up as itself
internal static class CarKey
{
    private const int EntryShift = 16;
    private const int ModelMask = 0xFFFF;

    public static car_carOrigin.ModelType Model(int key) => (car_carOrigin.ModelType)(key & ModelMask);

    // cheap enough for every frame, Of isn't
    public static int Signature(car_carOrigin origin) =>
        (int)origin.modelType + 1 | (int)origin.chassisType << 16 | (origin.hasSunroof ? 1 << 30 : 0);

    public static int Of(car_carOrigin origin)
    {
        var entries = GameMaster.CarOrigins?.entries_;
        var plain = (int)origin.modelType;
        if (entries == null)
            return plain;

        var sameModel = -1;
        for (var i = 0; i < entries.Count; i++)
        {
            var data = entries[i].Data;
            if (data == null || data.Model != origin.modelType || entries[i].AiOnly)
                continue;
            if (data.Chassis == origin.chassisType && data.HasSunroof == origin.hasSunroof)
                return Make(plain, i);
            if (sameModel < 0)
                sameModel = i;
        }
        return sameModel >= 0 ? Make(plain, sameModel) : plain;
    }

    // every car someone could be driving
    public static List<int> All()
    {
        var keys = new List<int>();
        var entries = GameMaster.CarOrigins?.entries_;
        for (var i = 0; entries != null && i < entries.Count; i++)
        {
            var data = entries[i].Data;
            if (data != null && !entries[i].AiOnly)
                keys.Add(Make((int)data.Model, i));
        }
        return keys;
    }

    // one of each model, the catalog has rows of the same car. the 911 FLA throws halfway through StockCarSpawn when it's a stock copy
    public static List<int> ForBots()
    {
        var keys = new List<int>();
        var seen = new HashSet<int>();
        foreach (var key in All())
            if (seen.Add(key & ModelMask) && !Model(key).ToString().Contains("911_FLA"))
                keys.Add(key);
        return keys;
    }

    public static int Make(int model, int entry) => model | (entry + 1) << EntryShift;

    // StockCarSpawn looks up its entry right as it starts, before the first wait, so later entries step aside just for that moment
    public static void SpawnExact(int key, Action spawn)
    {
        var entries = GameMaster.CarOrigins?.entries_;
        var model = Model(key);
        var wanted = (key >> EntryShift) - 1;
        var moved = new List<PlanetJem.Customization.CarOriginData>();
        if (entries != null && wanted >= 0 && wanted < entries.Count)
        {
            for (var i = wanted + 1; i < entries.Count; i++)
            {
                var data = entries[i].Data;
                if (data != null && data.Model == model)
                    moved.Add(data);
            }
        }

        foreach (var data in moved)
            data.Model = default;
        try
        {
            spawn();
        }
        finally
        {
            foreach (var data in moved)
                data.Model = model;
        }
    }
}
