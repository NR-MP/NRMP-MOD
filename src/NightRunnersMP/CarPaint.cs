using LiteNetLib.Utils;
using UnityEngine;

namespace NightRunnersMP;

// body, rim, second colour as bytes. server just stores and forwards
internal static class CarPaint
{
    public static byte[] Read(CarLocalCustom car)
    {
        var w = new NetDataWriter();
        Write(w, car.carPaint);
        Write(w, car.rimPaint_front);
        Write(w, car.rimPaint_rear);
        Write(w, car.carPaint_secondColour);
        return w.CopyData();
    }

    // same order as CarApplicator.TryApplyPaint. no material for a paint = leave it unset
    public static void Apply(GodConstant god, CarLocalCustom car, byte[] paint)
    {
        var r = new NetDataReader(paint);
        car.carPaint = Assign(god, r, car.carPaint_cache, car.carPaint);
        car.rimPaint_front = Assign(god, r, car.rimPaint_frontCache, car.rimPaint_front);
        car.rimPaint_rear = Assign(god, r, car.rimPaint_rearCache, car.rimPaint_rear);
        car.carPaint_secondColour = Assign(god, r, car.carPaint_secondCache, car.carPaint_secondColour);
    }

    private static void Write(NetDataWriter w, car_carOrigin.Car_Paint paint)
    {
        w.Put(paint != null);
        if (paint == null)
            return;
        Write(w, paint.main_colour);
        Write(w, paint.pearl_colour);
        w.Put((int)paint.paint_paintType);
    }

    private static void Write(NetDataWriter w, Color c)
    {
        w.Put(c.r);
        w.Put(c.g);
        w.Put(c.b);
        w.Put(c.a);
    }

    private static car_carOrigin.Car_Paint Assign(GodConstant god, NetDataReader r, Material material, car_carOrigin.Car_Paint current)
    {
        if (!r.GetBool())
            return current;
        var paint = new car_carOrigin.Car_Paint
        {
            main_colour = ReadColor(r),
            pearl_colour = ReadColor(r),
            paint_paintType = (GodConstant.PaintTypeCar)r.GetInt(),
        };
        return material != null ? god.carPaint_assignValues(paint, material) : current;
    }

    private static Color ReadColor(NetDataReader r) => new(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat());
}
