using System;
using System.Globalization;
using UnityEngine;

namespace ValheimPlanBuild;

internal sealed class PlanPiece
{
    public string Id { get; set; } = "";
    public string Prefab { get; set; } = "";
    public Vector3 Position { get; set; }
    public Quaternion Rotation { get; set; } = Quaternion.identity;
    public string Owner { get; set; } = "";
    public long CreatedUnixMs { get; set; }

    public string ToRecord()
    {
        return string.Join("\t",
            Escape(Id),
            Escape(Prefab),
            F(Position.x),
            F(Position.y),
            F(Position.z),
            F(Rotation.x),
            F(Rotation.y),
            F(Rotation.z),
            F(Rotation.w),
            Escape(Owner),
            CreatedUnixMs.ToString(CultureInfo.InvariantCulture));
    }

    public static bool TryParseRecord(string line, out PlanPiece piece)
    {
        piece = new PlanPiece();
        string[] parts = line.Split('\t');
        if (parts.Length < 11)
        {
            return false;
        }

        if (!TryFloat(parts[2], out float px) ||
            !TryFloat(parts[3], out float py) ||
            !TryFloat(parts[4], out float pz) ||
            !TryFloat(parts[5], out float rx) ||
            !TryFloat(parts[6], out float ry) ||
            !TryFloat(parts[7], out float rz) ||
            !TryFloat(parts[8], out float rw) ||
            !long.TryParse(parts[10], NumberStyles.Integer, CultureInfo.InvariantCulture, out long created))
        {
            return false;
        }

        piece.Id = Unescape(parts[0]);
        piece.Prefab = Unescape(parts[1]);
        piece.Position = new Vector3(px, py, pz);
        piece.Rotation = new Quaternion(rx, ry, rz, rw);
        piece.Owner = Unescape(parts[9]);
        piece.CreatedUnixMs = created;
        return piece.Id.Length > 0 && piece.Prefab.Length > 0;
    }

    private static string F(float value)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static bool TryFloat(string text, out float value)
    {
        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static string Escape(string value)
    {
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value ?? ""));
    }

    private static string Unescape(string value)
    {
        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value));
    }
}
