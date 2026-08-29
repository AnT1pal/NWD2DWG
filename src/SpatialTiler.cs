// ============================================================================
//  NWD2DWG — SpatialTiler.cs
//  Модуль пространственной нарезки модели на захватки (Spatial Grid Tiling).
//
//  Разработчик: Baidurov Pavel / BaidurovLabs
//  Лицензия: GNU General Public License v3.0 (GPLv3)
// ============================================================================

using System;
using System.Collections.Generic;

namespace NWD2DWG.Plugin
{
    public class SpatialTileKey : IEquatable<SpatialTileKey>
    {
        public int TileX, TileY, TileZ;

        public SpatialTileKey(int x, int y, int z) { TileX = x; TileY = y; TileZ = z; }

        public override bool Equals(object obj) => obj is SpatialTileKey k && Equals(k);
        public bool Equals(SpatialTileKey other) => other != null && TileX == other.TileX && TileY == other.TileY && TileZ == other.TileZ;
        public override int GetHashCode() => (TileX * 397 ^ TileY) * 397 ^ TileZ;
        public override string ToString() => string.Format("Tile_{0}_{1}_{2}", TileX, TileY, TileZ);
    }

    public static class SpatialTiler
    {
        /// <summary>
        /// Определение индекса захватки по центроиду полигона
        /// </summary>
        public static SpatialTileKey GetTileKey(double x, double y, double z, double tileSize = 20000.0) // 20 метров по умолчанию
        {
            int tx = (int)Math.Floor(x / tileSize);
            int ty = (int)Math.Floor(y / tileSize);
            int tz = (int)Math.Floor(z / tileSize);
            return new SpatialTileKey(tx, ty, tz);
        }
    }
}
