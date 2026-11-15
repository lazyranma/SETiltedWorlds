using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using Data;
using Game.Info;
using HarmonyLib;
using Manager;
using PlanetsScripts;
using UnityEngine;
using UnityEngine.SceneManagement;

using static TiltedWorlds.Plugin;
using static TiltedWorlds.BodyOrientation;
using static TiltedWorlds.OrbitPlanes;
using static TiltedWorlds.Magnetosphere;
using static TiltedWorlds.MagnetosphereGeometry;
using static UnityEngine.Object;

namespace TiltedWorlds
{
    internal static class BodyData
    {
        internal const double DegToRad = Math.PI / 180.0;
        internal const double RadToDeg = 180.0 / Math.PI;

        /// <summary>
        /// Cassini state 2: the Moon's spin axis is 6.687 deg from its orbit normal and lies in
        /// the plane of that normal and the ecliptic normal. The resulting ecliptic tilt
        /// follows the frozen orbital inclination selected below.
        /// Frozen node, frozen pole; precession is not modelled.
        /// </summary>
        internal const double MoonSpinAxisToOrbitNormal = 6.687;

        // Real axial tilt (obliquity) of planets and dwarf planets, degrees.
        internal static readonly Dictionary<string, double> PlanetTilts = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            { "Mercury", 0.034 },
            { "Venus", 177.36 },
            { "Earth", 23.4393 },
            { "Mars", 25.19 },
            { "Jupiter", 3.13 },
            { "Saturn", 26.73 },
            { "Uranus", 97.77 },
            { "Neptune", 28.32 },
            { "Pluto", 122.53 },
            { "Ceres", 4.0 },
        };

        /// <summary>
        /// Real magnetic axis in the body frame, epoch 2020. <c>Tilt</c> is the angle between the dipole
        /// axis and the spin axis, degrees; <c>LongitudeEast</c> is the body-fixed longitude the north
        /// end points at, when its prime meridian has been calibrated against the model's texture.
        ///
        /// Sources: Earth — IGRF-13 geomagnetic pole 80.65 N, 72.68 W (colatitude 9.35); Jupiter — the
        /// classic offset tilted dipole, 9.6 deg (Connerney 1981); Uranus — Voyager Q3, 58.6 deg
        /// (Ness et al. 1986); Neptune — 47 deg (Ness et al. 1989); Ganymede — the moment points
        /// 176 deg from the spin axis, so the axis *line* is 4 deg off.
        ///
        /// Earth's texture and both mesh LODs put Greenwich on local +X and 90 E on local +Z.
        /// The other bodies' prime meridians have not been calibrated. Earth's spin and magnetic
        /// axes cannot determine another body's rotation phase, so we cannot establish which way
        /// to tilt their poles on the surface. Apply the known tilt without an azimuth rotation:
        /// the resulting direction is arbitrary, not a claim about their magnetic longitudes.
        ///
        /// Mercury (dipole aligned with the spin axis within the MESSENGER bound of 2.5 deg) and Saturn
        /// (Cassini: strictly aligned, < 0.5 deg) are omitted, and so is every body without an intrinsic
        /// field. Those keep the game's spin-aligned visualization convention.
        /// </summary>
        internal static readonly Dictionary<string, MagneticAxis> MagneticAxes =
            new Dictionary<string, MagneticAxis>(StringComparer.Ordinal)
        {
            { "Earth", new MagneticAxis(9.35, -72.68) },
            { "Jupiter", new MagneticAxis(9.6) },
            { "Uranus", new MagneticAxis(58.6) },
            { "Neptune", new MagneticAxis(47.0) },
            { "Ganymede", new MagneticAxis(4.0) },
        };

        internal readonly struct MagneticAxis
        {
            public readonly double Tilt;
            // Only set for a texture frame with prime meridian +X and east toward +Z.
            public readonly double? LongitudeEast;

            public MagneticAxis(double tilt, double? longitudeEast = null)
            {
                Tilt = tilt;
                LongitudeEast = longitudeEast;
            }
        }

        /// <summary>
        /// Whether to rotate the magnetic axis off the spin axis by the observed offset in
        /// <see cref="MagneticAxes"/>. False ignores that table, so auroras and magnetospheres keep
        /// the vanilla convention and follow the rotation axis (the body's axial tilt still applies).
        /// </summary>
        internal static bool TiltMagneticAxes = true;

        // JPL Horizons osculating orbital planes: https://ssd-api.jpl.nasa.gov/doc/horizons.html
        // All moons: 2020-01-01 00:00:00 TDB (JD 2458849.5), planet-centered J2000 ecliptic.
        // Inclination and ascending node are paired at the same epoch and in the same frame.
        // Frozen planes, no precession.
        // Semi-major axes, eccentricities, periapsis arguments and phases remain game-authored.
        internal static readonly Dictionary<string, MoonOrbit> MoonOrbits = new Dictionary<string, MoonOrbit>(StringComparer.Ordinal)
        {
            { "Moon",       new MoonOrbit(5.2862312659, 98.1405197992) },
            { "Phobos",     new MoonOrbit(25.6336474139, 82.6516250750) },
            { "Deimos",     new MoonOrbit(24.9986558692, 79.0605614275) },
            { "Io",         new MoonOrbit(2.2538042234, 338.1607153350) },
            { "Europa",     new MoonOrbit(2.6228798307, 332.4238155819) },
            { "Ganymede",   new MoonOrbit(2.3064801508, 340.3586434586) },
            { "Callisto",   new MoonOrbit(1.9648319191, 336.8965671052) },
            { "Amalthea",   new MoonOrbit(2.5890879034, 338.7759956367) },
            { "Mimas",      new MoonOrbit(29.3917191231, 171.2345072977) },
            { "Enceladus",  new MoonOrbit(28.0414062003, 169.5306044665) },
            { "Tethys",     new MoonOrbit(27.1594277954, 168.1616802920) },
            { "Dione",      new MoonOrbit(28.0760762702, 169.5237287600) },
            { "Rhea",       new MoonOrbit(27.9083526386, 170.1615061041) },
            { "Titan",      new MoonOrbit(27.6980671929, 169.0713418055) },
            { "Hyperion",   new MoonOrbit(27.0342683575, 169.1462998726) },
            { "Iapetus",    new MoonOrbit(17.0803309455, 138.8516457958) },
            { "Triton",     new MoonOrbit(129.3377021553, 221.0838868382) },
            { "Nereid",     new MoonOrbit(5.0252485690, 319.5589220022) },
            { "Proteus",    new MoonOrbit(29.0399764691, 48.7477388656) },
            { "Charon",     new MoonOrbit(112.8881740732, 227.3925114917) },
            { "Puck",       new MoonOrbit(96.8218441896, 167.1084743638) },
            { "Ariel",      new MoonOrbit(97.7165398112, 167.6406839434) },
            { "Umbriel",    new MoonOrbit(97.6927450200, 167.7102680874) },
            { "Titania",    new MoonOrbit(97.7758876065, 167.6317633788) },
            { "Oberon",     new MoonOrbit(97.9053151991, 167.7217205074) },
        };

        internal readonly struct MoonOrbit
        {
            // Both angles are referred to the J2000 ecliptic.
            public readonly double Inclination;
            public readonly double AscendingNode;

            public MoonOrbit(double inclination, double ascendingNode)
            {
                Inclination = inclination;
                AscendingNode = ascendingNode;
            }
        }

        internal static readonly HashSet<string> KnownBodies = BuildKnownBodies();

        private static HashSet<string> BuildKnownBodies()
        {
            var set = new HashSet<string>(PlanetTilts.Keys, StringComparer.Ordinal);
            foreach (string key in MoonOrbits.Keys)
            {
                set.Add(key);
            }
            return set;
        }


        // Real poles: IAU right ascension and declination in degrees. These are the published
        // "north" poles (Pluto's is NASA's positive pole); the model's own north is aimed at them,
        // and the spin direction stays whatever the game authored (retrograde on Venus).
        internal static readonly Dictionary<string, (double Ra, double Dec)> PlanetAxes =
            new Dictionary<string, (double Ra, double Dec)>(StringComparer.Ordinal)
        {
            { "Mercury", (281.01, 61.41) },
            { "Venus", (272.76, 67.16) },
            { "Mars", (317.68, 52.89) },
            { "Jupiter", (268.06, 64.50) },
            { "Saturn", (40.59, 83.54) },
            { "Uranus", (257.31, -15.18) },
            { "Neptune", (299.33, 42.95) },
            { "Pluto", (132.99, -6.16) },
            { "Ceres", (291.42, 66.76) },
        };

        internal const double RealEarthPoleLongitude = 90.0;
    }

}
