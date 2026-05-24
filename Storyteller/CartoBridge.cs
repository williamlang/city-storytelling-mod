using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Colossal.Logging;
using Game.Modding;
using Game.SceneFlow;

namespace CityStoryMod.Storyteller
{
    // Reflective wrapper around Carto's peer-mod export API. Carto's upstream
    // peer-API now takes a Carto.IO.Options instance directly:
    //
    //   Carto.IO.IO.Export(Carto.IO.Options) → Carto.IO.ExportResult
    //
    // Options has 30+ fields covering projection, content selection, and side
    // effects. We construct a minimal one with sensible defaults plus the
    // handful of fields we actually care about
    // (Systems=Area+Building+Network+Raster,
    // Features=District+Building+MapTile+Road,
    // VectorFormat=GeoJSON, RasterFormat=GeoTIFF,
    // RasterKinds=Elevation+Depth, CompletionDialog/Sound=false).
    //
    // Soft assembly coupling: no compile-time reference to Carto.dll, so our
    // DLL survives Carto rebuilds across CS2 patches as long as the public
    // API surface (Carto.IO.Options + IO.Export(Options) + ExportResult) is
    // stable. If anything we probe for goes missing, the bridge logs an
    // actionable "update Carto" message and disables itself — the Refresh
    // map button stays hidden.
    public static class CartoBridge
    {
        const string CartoAssemblyName = "Carto";
        const string OptionsTypeName = "Carto.IO.Options";
        const string ExportResultTypeName = "Carto.IO.ExportResult";
        const string IoTypeName = "Carto.IO.IO";
        const string FileFormatTypeName = "Carto.IO.FileFormat";
        const string FeatureTypeName = "Carto.IO.Feature";
        const string SystemTypeName = "Carto.IO.System";
        const string VectorKindTypeName = "Carto.IO.VectorKind";
        const string RasterKindTypeName = "Carto.IO.RasterKind";
        const string PropertyEnumTypeName = "Carto.IO.Property";
        const string ErrorEnumTypeName = "Carto.IO.Error";
        const string CoordTypeName = "Carto.Geodata.Coord";
        const string HemisphereTypeName = "Carto.Geodata.Hemisphere";

        static bool _resolved;
        static bool _available;
        static string _version;

        // Resolved Carto types — kept around so BuildOptions can construct
        // value-typed dictionary keys and enum values without re-probing on
        // every export.
        static Type _optionsType;
        static Type _systemEnumType;
        static Type _featureEnumType;
        static Type _vectorKindEnumType;
        static Type _rasterKindEnumType;
        static Type _propertyEnumType;
        static Type _errorEnumType;
        static MethodInfo _exportMethod;

        // ExportResult property getters.
        static PropertyInfo _resSuccess, _resFiles, _resError;

        // Pre-computed enum values for our request shape. Looked up by name at
        // resolve time so we survive Carto reordering bit positions.
        static object _formatGeoJson;
        static object _formatGeoTiff;
        static object _formatUnknown;
        static object _systemAreaValue;
        static object _systemBuildingValue;
        static object _systemNetworkValue;
        static object _systemsCombinedValue;
        static object _featuresCombinedValue;
        static object _vectorKindBoundary;
        static object _vectorKindCenterline;
        static object _rasterKindsCombined;
        // Property enum values we want emitted on districts, buildings, networks.
        static object[] _propertyValuesForArea;
        static object[] _propertyValuesForBuilding;
        static object[] _propertyValuesForNetwork;

        // A pre-built Coord(0, 0, Hemisphere.North, 31) used as SourceCoordinates.
        // The default Options.SourceCoordinates has crs = CRS.Game, which makes
        // TagManager's tiepoint.Hemisphere getter throw a NotSupportedException
        // when the GeoTIFF writer runs (vector exports don't go through
        // TagManager so they were silently fine). The 4-arg ctor produces a
        // UTM-CRS Coord that Carto's metadata writer accepts. Zone 31 is
        // arbitrary — we don't use the projected coordinates downstream.
        static object _utmSourceCoordinates;

        public static bool IsAvailable
        {
            get
            {
                EnsureResolved();
                return _available;
            }
        }

        public static string Version
        {
            get
            {
                EnsureResolved();
                return _version;
            }
        }

        public class Result
        {
            public bool Success;
            public string[] FilesWritten;
            public string ErrorMessage;
        }

        // Returns null when Carto is unavailable. Otherwise returns the
        // (possibly failed) result from Carto's export pipeline — caller
        // distinguishes Success vs ErrorMessage.
        public static Result TryExport(string outputDirectory, ILog log)
        {
            EnsureResolved();
            if (!_available) return null;

            try
            {
                object options = BuildOptions(outputDirectory);
                object result = _exportMethod.Invoke(null, new[] { options });
                return new Result
                {
                    Success = (bool)_resSuccess.GetValue(result),
                    FilesWritten = (string[])_resFiles.GetValue(result) ?? new string[0],
                    ErrorMessage = (string)_resError.GetValue(result),
                };
            }
            catch (Exception ex)
            {
                log?.Error(ex, "CartoBridge.TryExport failed.");
                return new Result { Success = false, ErrorMessage = ex.Message };
            }
        }

        // Constructs a Carto.IO.Options reflectively, filling in the minimum
        // set of fields the export pipeline needs to run for districts. Every
        // field we don't set is left at its in-class default — which the new
        // upstream API has set to sensible values (proper UTM projection
        // params on Target/SourceProjectionDefinition, etc.).
        //
        // Fields explicitly set (everything else uses Options' class defaults):
        //   CustomDirectory     — required, no default
        //   Systems / Features  — what to export
        //   VectorFormat        — GeoJSON
        //   RasterFormat / RasterKinds — Unknown (we don't want rasters)
        //   VectorKinds         — { Area → Boundary }
        //   Properties          — { Area → {Name, Object, Resident, Employee, Unlocked} }
        //   Display             — { (Object, Unknown) → false }  (avoids NRE in pipeline)
        //   CompletionDialog    — false  (peer-API hygiene; default is true)
        //   CompletionSound     — false  (peer-API hygiene; default is true)
        //   Created             — now
        //   Errors              — new empty dictionary
        static object BuildOptions(string outputDirectory)
        {
            object opts = Activator.CreateInstance(_optionsType);

            Set(opts, "CustomDirectory", outputDirectory);
            Set(opts, "Systems", _systemsCombinedValue);
            Set(opts, "Features", _featuresCombinedValue);
            Set(opts, "VectorFormat", _formatGeoJson);
            Set(opts, "RasterFormat", _formatGeoTiff);
            Set(opts, "RasterKinds", _rasterKindsCombined);
            // Override default SourceCoordinates (crs=Game, which breaks the
            // GeoTIFF tag writer). See _utmSourceCoordinates declaration.
            if (_utmSourceCoordinates != null) Set(opts, "SourceCoordinates", _utmSourceCoordinates);
            Set(opts, "CompletionDialog", false);
            Set(opts, "CompletionSound", false);
            Set(opts, "Created", DateTime.Now);
            // Per-feature filenames. The peer-API Options default
            // (FileName = "output") collapses every system+vectorkind into a
            // single output.json which both clobbers itself across features
            // and doesn't match what the existing processor reads (e.g.
            // Area_Boundary.json). The token gets replaced by "<System>_<Kind>"
            // at write time — see Carto's Options.GetFilePath.
            Set(opts, "FileName", "{Feature}");

            // Errors: Dictionary<string, Carto.IO.Error>.
            Type errorsType = typeof(System.Collections.Generic.Dictionary<,>)
                .MakeGenericType(typeof(string), _errorEnumType);
            Set(opts, "Errors", Activator.CreateInstance(errorsType));

            // VectorKinds: Dictionary<Carto.IO.System, Carto.IO.VectorKind>
            //   Area    → Boundary       (district polygons + MapTile polygons)
            //   Building→ Boundary       (named civic/service building outlines)
            //   Network → Centerline     (road/track linestrings down the middle)
            // Carto's NetworkSystem only writes Centerline output — Boundary
            // would give us road footprints, but for storytelling the centerline
            // is what carries length, name, and adjacency information.
            Type vectorKindsType = typeof(System.Collections.Generic.Dictionary<,>)
                .MakeGenericType(_systemEnumType, _vectorKindEnumType);
            IDictionary vectorKinds = (IDictionary)Activator.CreateInstance(vectorKindsType);
            vectorKinds[_systemAreaValue] = _vectorKindBoundary;
            vectorKinds[_systemBuildingValue] = _vectorKindBoundary;
            vectorKinds[_systemNetworkValue] = _vectorKindCenterline;
            Set(opts, "VectorKinds", vectorKinds);

            // Properties: Dictionary<Carto.IO.System, HashSet<Carto.IO.Property>>
            //   Area     → {Name, Object, Resident, Employee, Unlocked}
            //   Building → {Name, Object, Category, Resident, Employee}
            //   Network  → {Name, Object, Category, Form, Length, Lane, Limit}
            Type hashSetOfPropertyType = typeof(System.Collections.Generic.HashSet<>)
                .MakeGenericType(_propertyEnumType);
            MethodInfo addToSet = hashSetOfPropertyType.GetMethod("Add", new[] { _propertyEnumType });

            IEnumerable areaPropertySet = (IEnumerable)Activator.CreateInstance(hashSetOfPropertyType);
            foreach (object propValue in _propertyValuesForArea) addToSet.Invoke(areaPropertySet, new[] { propValue });

            IEnumerable buildingPropertySet = (IEnumerable)Activator.CreateInstance(hashSetOfPropertyType);
            foreach (object propValue in _propertyValuesForBuilding) addToSet.Invoke(buildingPropertySet, new[] { propValue });

            IEnumerable networkPropertySet = (IEnumerable)Activator.CreateInstance(hashSetOfPropertyType);
            foreach (object propValue in _propertyValuesForNetwork) addToSet.Invoke(networkPropertySet, new[] { propValue });

            Type propertiesType = typeof(System.Collections.Generic.Dictionary<,>)
                .MakeGenericType(_systemEnumType, hashSetOfPropertyType);
            IDictionary properties = (IDictionary)Activator.CreateInstance(propertiesType);
            properties[_systemAreaValue] = areaPropertySet;
            properties[_systemBuildingValue] = buildingPropertySet;
            properties[_systemNetworkValue] = networkPropertySet;
            Set(opts, "Properties", properties);

            // Display: Dictionary<(Carto.IO.Property, Carto.IO.System), bool>.
            // Carto's per-system writers index into this dict for several
            // (Property, System) combinations and KeyNotFoundException if any
            // are missing. We populate all five entries the upstream
            // FromRequest helper used to set — same shape as the button path
            // produces from its UI defaults. Missing even one of these
            // crashes the export with an exception that BuildingSystem then
            // swallows in its catch block (also calling DisposeAll, which
            // destroys shared state and makes the next writer fail too).
            Type tupleType = typeof(ValueTuple<,>).MakeGenericType(_propertyEnumType, _systemEnumType);
            Type displayType = typeof(System.Collections.Generic.Dictionary<,>)
                .MakeGenericType(tupleType, typeof(bool));
            IDictionary display = (IDictionary)Activator.CreateInstance(displayType);

            void AddDisplay(string propertyName, string systemName, bool value)
            {
                object propValue = Enum.Parse(_propertyEnumType, propertyName);
                object sysValue = Enum.Parse(_systemEnumType, systemName);
                object key = Activator.CreateInstance(tupleType, propValue, sysValue);
                display[key] = value;
            }
            AddDisplay("Category", "Building", true);
            AddDisplay("Category", "Network", true);
            AddDisplay("Category", "POI", true);
            AddDisplay("Object", "Unknown", false);
            AddDisplay("Zoning", "Unknown", true);

            Set(opts, "Display", display);

            return opts;
        }

        static void Set(object instance, string propertyName, object value)
        {
            PropertyInfo p = _optionsType.GetProperty(propertyName);
            if (p == null)
                throw new InvalidOperationException($"Carto.IO.Options.{propertyName} not found — Carto API drift.");
            p.SetValue(instance, value);
        }

        static void EnsureResolved()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                Resolve();
            }
            catch (Exception ex)
            {
                Mod.Log?.Error(ex, "CartoBridge.Resolve threw.");
                _available = false;
            }
        }

        static void Resolve()
        {
            var modManager = GameManager.instance?.modManager;
            if (modManager == null)
            {
                Mod.Log?.Info("CartoBridge: modManager unavailable; geography exports disabled.");
                return;
            }

            Assembly asm = null;
            foreach (ModManager.ModInfo mod in modManager)
            {
                if (mod.name != null && mod.name.StartsWith(CartoAssemblyName, StringComparison.Ordinal))
                {
                    asm = mod.asset?.assembly;
                    break;
                }
            }
            if (asm == null)
            {
                Mod.Log?.Info("CartoBridge: Carto not installed; geography exports disabled.");
                return;
            }

            string version = asm.GetName().Version.ToString();

            // Capability-based compatibility check. We probe for every type,
            // method, property, and enum value we need; if any is missing,
            // surface a single actionable message: "update Carto via
            // Paradox Mods."

            _optionsType = asm.GetType(OptionsTypeName);
            Type exportResultType = asm.GetType(ExportResultTypeName);
            Type ioType = asm.GetType(IoTypeName);
            Type fileFormatType = asm.GetType(FileFormatTypeName);
            _featureEnumType = asm.GetType(FeatureTypeName);
            _systemEnumType = asm.GetType(SystemTypeName);
            _vectorKindEnumType = asm.GetType(VectorKindTypeName);
            _rasterKindEnumType = asm.GetType(RasterKindTypeName);
            _propertyEnumType = asm.GetType(PropertyEnumTypeName);
            _errorEnumType = asm.GetType(ErrorEnumTypeName);

            if (_optionsType == null || exportResultType == null || ioType == null || fileFormatType == null
                || _featureEnumType == null || _systemEnumType == null || _vectorKindEnumType == null
                || _rasterKindEnumType == null || _propertyEnumType == null || _errorEnumType == null)
            {
                LogIncompatible(version, "peer-API types missing (Carto.IO.Options / ExportResult / IO / FileFormat / Feature / System / VectorKind / RasterKind / Property / Error)");
                return;
            }

            _exportMethod = ioType.GetMethod("Export", new[] { _optionsType });
            if (_exportMethod == null)
            {
                LogIncompatible(version, "Carto.IO.IO.Export(Options) not found");
                return;
            }

            _resSuccess = exportResultType.GetProperty("Success");
            _resFiles = exportResultType.GetProperty("FilesWritten");
            _resError = exportResultType.GetProperty("ErrorMessage");
            if (_resSuccess == null || _resFiles == null || _resError == null)
            {
                LogIncompatible(version, "ExportResult properties missing");
                return;
            }

            // Enum-value lookups. Wrapped so a missing name surfaces as an
            // incompatibility message instead of a raw exception.
            if (!TryParseEnum(fileFormatType, "GeoJSON", out _formatGeoJson)
                || !TryParseEnum(fileFormatType, "GeoTIFF", out _formatGeoTiff)
                || !TryParseEnum(fileFormatType, "Unknown", out _formatUnknown)
                || !TryParseEnum(_systemEnumType, "Area", out _systemAreaValue)
                || !TryParseEnum(_systemEnumType, "Building", out _systemBuildingValue)
                || !TryParseEnum(_systemEnumType, "Network", out _systemNetworkValue)
                || !TryCombineFlags(_systemEnumType, out _systemsCombinedValue, "Area", "Building", "Network", "Raster")
                || !TryCombineFlags(_featureEnumType, out _featuresCombinedValue, "District", "Building", "MapTile", "Road")
                || !TryParseEnum(_vectorKindEnumType, "Boundary", out _vectorKindBoundary)
                || !TryParseEnum(_vectorKindEnumType, "Centerline", out _vectorKindCenterline)
                || !TryCombineFlags(_rasterKindEnumType, out _rasterKindsCombined, "Elevation", "Depth"))
            {
                LogIncompatible(version, "expected enum values missing (FileFormat.GeoJSON+GeoTIFF+Unknown / System.Area+Building+Network+Raster / Feature.District+Building+MapTile+Road / VectorKind.Boundary+Centerline / RasterKind.Elevation+Depth)");
                return;
            }

            // Property enum values for the emitted attributes per system.
            // Mirrors what upstream's prior BuildDefaultProperties produced
            // for these systems — just the fields the processor uses.
            string[] areaPropNames = { "Name", "Object", "Resident", "Employee", "Unlocked" };
            string[] buildingPropNames = { "Name", "Object", "Category", "Resident", "Employee" };
            string[] networkPropNames = { "Name", "Object", "Category", "Form", "Length", "Lane", "Limit" };
            if (!TryResolvePropertyEnumValues(version, areaPropNames, out _propertyValuesForArea)) return;
            if (!TryResolvePropertyEnumValues(version, buildingPropNames, out _propertyValuesForBuilding)) return;
            if (!TryResolvePropertyEnumValues(version, networkPropNames, out _propertyValuesForNetwork)) return;

            // Build the UTM-CRS SourceCoordinates Coord we'll inject into
            // every Options instance. Required for GeoTIFF metadata writes —
            // see _utmSourceCoordinates field doc. Failure here disables
            // raster exports but leaves vector exports working.
            Type coordType = asm.GetType(CoordTypeName);
            Type hemisphereType = asm.GetType(HemisphereTypeName);
            if (coordType != null && hemisphereType != null)
            {
                var ctor = coordType.GetConstructor(new[] { typeof(double), typeof(double), hemisphereType, typeof(int) });
                if (ctor != null && TryParseEnum(hemisphereType, "North", out object northValue))
                {
                    try { _utmSourceCoordinates = ctor.Invoke(new object[] { 0.0, 0.0, northValue, 31 }); }
                    catch (Exception ex) { Mod.Log?.Warn($"CartoBridge: building UTM SourceCoordinates failed: {ex.Message}; rasters will throw."); }
                }
                else
                {
                    Mod.Log?.Warn("CartoBridge: Coord(double,double,Hemisphere,int) ctor or Hemisphere.North missing; rasters will throw.");
                }
            }
            else
            {
                Mod.Log?.Warn("CartoBridge: Carto.Geodata.Coord / Hemisphere types missing; rasters will throw.");
            }

            _version = version;
            _available = true;
            Mod.Log?.Info($"CartoBridge: detected Carto {_version}; geography exports enabled.");
        }

        static void LogIncompatible(string version, string detail)
        {
            Mod.Log?.Warn(
                $"CartoBridge: Carto {version} is installed but the peer-API CityStoryMod needs is missing "
                + $"({detail}). Update Carto via Paradox Mods. Geography exports disabled."
            );
        }

        static bool TryParseEnum(Type enumType, string name, out object value)
        {
            try { value = Enum.Parse(enumType, name); return true; }
            catch { value = null; return false; }
        }

        static bool TryCombineFlags(Type enumType, out object combined, params string[] names)
        {
            long bits = 0;
            foreach (string name in names)
            {
                if (!TryParseEnum(enumType, name, out object value)) { combined = null; return false; }
                bits |= Convert.ToInt64(value);
            }
            combined = Enum.ToObject(enumType, bits);
            return true;
        }

        static bool TryResolvePropertyEnumValues(string version, string[] propNames, out object[] values)
        {
            values = new object[propNames.Length];
            for (int i = 0; i < propNames.Length; i++)
            {
                if (!TryParseEnum(_propertyEnumType, propNames[i], out values[i]))
                {
                    LogIncompatible(version, $"Carto.IO.Property.{propNames[i]} missing");
                    return false;
                }
            }
            return true;
        }
    }
}
