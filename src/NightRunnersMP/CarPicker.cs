using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Logging;
using PlanetJem.Core;
using PlanetJem.Customization;
using UnityEngine;

namespace NightRunnersMP;

// every car in the files, free, the way the dealer does it after a sale: build, then saveCar_firstTime
// (garage with room, else storage). normal owned car so it stays in the save. save gets copied once before the first one
internal sealed class CarPicker
{
    private const float BuildTimeoutSeconds = 120f;
    private const float SaveTimeoutSeconds = 20f;
    private const float BuildDepth = 200f;
    private const string SaveFile = "SaveFile.es3";
    private const string BackupFile = "SaveFile.before-all-cars.es3";

    private readonly ManualLogSource _log;
    private readonly RemoteCars _cars;
    private List<Row> _rows;
    private Remote _order;
    private bool _orderAuto;
    private GameObject _built;
    private int _ownedBefore;
    private float _deadline;

    public string Status { get; private set; } = "Pick a car to add it to your garage";
    // manual or auto for the next car
    public bool Auto { get; set; }

    public CarPicker(ManualLogSource log, RemoteCars cars)
    {
        _log = log;
        _cars = cars;
    }

    public bool IsOwned(int model)
    {
        var owned = GodConstant.Instance?.allOwnedCars;
        if (owned == null)
            return false;
        foreach (var car in owned)
            if (car.modelType == CarKey.Model(model))
                return true;
        return false;
    }

    public void Owned(HashSet<car_carOrigin.ModelType> into)
    {
        into.Clear();
        var owned = GodConstant.Instance?.allOwnedCars;
        if (owned == null)
            return;
        foreach (var car in owned)
            into.Add(car.modelType);
    }

    // AI twins share a model with a player entry, and the starter entry isn't for sale
    public IReadOnlyList<Row> Rows
    {
        get
        {
            if (_rows != null)
                return _rows;
            var origins = GameMaster.CarOrigins;
            if (origins == null || origins.entries_ == null)
                return System.Array.Empty<Row>();

            var models = new SortedDictionary<string, Row>();
            for (var i = 0; i < origins.entries_.Count; i++)
            {
                var entry = origins.entries_[i];
                var data = entry.Data;
                if (data != null && !data.NotAPlayerCar && !entry.AiOnly)
                    models[Title(data.Model)] = new Row
                    {
                        Model = CarKey.Make((int)data.Model, i), Name = Title(data.Model), Details = Details(data),
                        HasManual = data.TransGearCount > 0, HasAuto = data.TransGearCountAuto > 0,
                    };
            }
            _rows = models.Values.ToList();
            return _rows;
        }
    }

    public void Pick(Row row, RCC_CarControllerV3 localCar)
    {
        if (_order != null || _built != null)
            return;
        if (GodConstant.Instance == null || localCar == null)
            Status = "Load into the game first";
        else if (!BackUpSave())
            Status = "Could not back up the save, no car added";
        else
            Order(row, localCar);
    }

    public void Tick()
    {
        if (_built != null)
            WatchSave();
        else if (_order != null)
            WatchBuild();
    }

    private void Order(Row row, RCC_CarControllerV3 localCar)
    {
        _orderAuto = row.HasAuto && (Auto || !row.HasManual);
        Status = $"Building the {row.Name} ({Gearbox(_orderAuto)})";
        _deadline = Time.unscaledTime + BuildTimeoutSeconds;
        _order = new Remote
        {
            Name = row.Name,
            Model = row.Model,
            LocalPosition = localCar.transform.position + Vector3.down * BuildDepth,
            Built = Save,
        };
        _cars.Request(_order);
    }

    // queue drops orders that spawned the wrong model, ask again here
    private void WatchBuild()
    {
        if (_cars.HasFailed(_order.Model) || Time.unscaledTime >= _deadline)
        {
            Status = $"The game could not build the {_order.Name}";
            _log.LogError(Status);
            _order = null;
            return;
        }
        _cars.Request(_order);
    }

    private void Save(GameObject car)
    {
        var god = GodConstant.Instance;
        var controller = car.GetComponent<RCC_CarControllerV3>();
        if (god == null || controller == null || controller.carLocal == null)
        {
            Object.Destroy(car);
            _order = null;
            return;
        }

        foreach (var renderer in car.GetComponentsInChildren<Renderer>(true))
            renderer.enabled = false;
        // same flag the game's own spawn takes, it goes into the save with the rest of the car
        if (controller.carData != null)
            controller.carData.trans_isAutoGear = _orderAuto;
        controller.automaticGear = _orderAuto;
        _built = car;
        _ownedBefore = god.allOwnedCars.Count;
        _deadline = Time.unscaledTime + SaveTimeoutSeconds;
        god.StartCoroutine(god.saveCar_firstTime(controller.carLocal, true));
    }

    private void WatchSave()
    {
        var god = GodConstant.Instance;
        var saved = god != null && god.allOwnedCars.Count > _ownedBefore;
        if (!saved && Time.unscaledTime < _deadline)
            return;

        if (saved)
        {
            god.saveGame();
            var place = god.allOwnedCars[god.allOwnedCars.Count - 1].carStorageLocation == homegarageSpot.HouseSaveID._CAR_STORAGE
                ? "car storage" : "your garage";
            Status = $"The {_order.Name} ({Gearbox(_orderAuto)}) is waiting in {place}";
        }
        else
        {
            Status = $"The game would not save the {_order.Name}";
        }
        _log.LogInfo($"{Status} (model {_order.Model})");
        Object.Destroy(_built);
        _built = null;
        _order = null;
    }

    // cars sitting in the storage lot, not the one you drive and not ones in a garage
    public List<Stored> InStorage()
    {
        var list = new List<Stored>();
        var god = GodConstant.Instance;
        if (god?.allOwnedCars == null)
            return list;
        foreach (var car in god.allOwnedCars)
            if (car.carStorageLocation == homegarageSpot.HouseSaveID._CAR_STORAGE && car.saveID != god.player_currentDrivingCarSaveID)
                list.Add(new Stored { SaveId = car.saveID, Name = Title(car.modelType) });
        return list;
    }

    // the game's own delete for a car's save, then off the owned list if it's still there. save gets copied once first, same as adding
    public void Delete(Stored car)
    {
        var god = GodConstant.Instance;
        if (god == null || _order != null || _built != null)
            return;
        if (!BackUpSave())
        {
            Status = "Could not back up the save, nothing deleted";
            return;
        }
        god.CarDeleteSave(car.SaveId, true);
        for (var i = god.allOwnedCars.Count - 1; i >= 0; i--)
            if (god.allOwnedCars[i].saveID == car.SaveId)
                god.allOwnedCars.RemoveAt(i);
        ES3.Save("allOwnedCars", god.allOwnedCars, god.es3_settings);
        god.saveGame();
        god.refreshCacheAfterSave();
        Status = $"Deleted the {car.Name} from car storage";
        _log.LogInfo($"{Status} (save {car.SaveId})");
    }

    private bool BackUpSave()
    {
        var folder = Application.persistentDataPath;
        var backup = Path.Combine(folder, BackupFile);
        try
        {
            if (!File.Exists(backup))
                File.Copy(Path.Combine(folder, SaveFile), backup);
            return true;
        }
        catch (IOException e)
        {
            _log.LogError($"Save backup failed: {e.Message}");
            return false;
        }
    }

    public static string Gearbox(bool auto) => auto ? "automatic" : "manual";

    private static string Title(car_carOrigin.ModelType model) =>
        model.ToString().Replace("_TEMP_NAME", "").Replace('_', ' ').ToUpperInvariant();

    // right off the catalog entry, so no car has to load for the list. stock numbers, what the car comes with
    private static string Details(CarOriginData data)
    {
        var parts = new List<string>();
        if (data.ModelYear > 0)
            parts.Add(data.ModelYear.ToString());
        parts.Add(data.StockDrivetrain.ToString());
        parts.Add(data.EnginePosition.ToString().Replace('_', ' ').ToLowerInvariant() + " engine");
        if (data.TransGearCount > 0)
            parts.Add($"{data.TransGearCount} speed manual");
        if (data.TransGearCountAuto > 0)
            parts.Add($"{data.TransGearCountAuto} speed auto");
        var engine = StockEngine(data);
        if (engine != null)
            parts.Add(engine);
        return string.Join("  |  ", parts);
    }

    private static string StockEngine(CarOriginData data)
    {
        if (data.Engines == null)
            return null;
        foreach (var engine in data.Engines)
            if (engine != null && engine.Present)
                return string.IsNullOrEmpty(engine.SpecLabel) ? engine.Type.ToString().Replace('_', ' ') : engine.SpecLabel;
        return null;
    }

    internal sealed class Stored
    {
        public int SaveId;
        public string Name;
    }

    internal sealed class Row
    {
        public int Model;
        public string Name;
        public string Details;
        public bool HasManual;
        public bool HasAuto;
    }
}
