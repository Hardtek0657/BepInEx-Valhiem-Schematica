using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace ValheimPlanBuild;

internal static class PlanBuildFrame
{
    public const string Prefix = "PLANBUILD\t2\t";

    public static string Place(string clientId, string name, PlanPiece piece)
    {
        return Prefix + string.Join("\t", Encode(clientId), Encode(GetWorldKey()), "PLACE", Encode(name), PieceFields(piece));
    }

    public static string Remove(string clientId, string name, string id)
    {
        return Prefix + string.Join("\t", Encode(clientId), Encode(GetWorldKey()), "REMOVE", Encode(name), Encode(id));
    }

    public static string Save(string clientId, string name, IReadOnlyList<PlanPiece> pieces)
    {
        // Pre-allocate list capacity: 5 header fields + 11 fields per piece
        List<string> fields = new(5 + (pieces.Count * 11))
        {
            Encode(clientId),
            Encode(GetWorldKey()),
            "SAVE",
            Encode(name),
            pieces.Count.ToString(CultureInfo.InvariantCulture)
        };
        
        for (int i = 0; i < pieces.Count; i++)
        {
            fields.AddRange(PieceFieldsArray(pieces[i]));
        }

        return Prefix + string.Join("\t", fields);
    }

    public static string LoadRequest(string clientId, string name)
    {
        return Prefix + string.Join("\t", Encode(clientId), Encode(GetWorldKey()), "LOAD", Encode(name));
    }

    public static string Hello(string clientId)
    {
        return Prefix + string.Join("\t", Encode(clientId), Encode(GetWorldKey()), "HELLO");
    }

    public static bool TryParse(string frame, out string clientId, out string worldKey, out string op, out string name, out PlanPiece? piece, out List<PlanPiece> pieces, out string removeId)
    {
        clientId = "";
        worldKey = "";
        op = "";
        name = "";
        piece = null;
        pieces = new List<PlanPiece>();
        removeId = "";

        if (!frame.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string[] parts = frame.Substring(Prefix.Length).Split('\t');
        if (parts.Length < 3)
        {
            return false;
        }

        clientId = Decode(parts[0]);
        worldKey = Decode(parts[1]);
        op = parts[2];
        int payloadIndex = 3;
        if (op == "PLACE")
        {
            if (parts.Length <= payloadIndex)
            {
                return false;
            }

            name = Decode(parts[payloadIndex]);
            return TryParsePiece(parts, payloadIndex + 1, out piece);
        }

        if (op == "REMOVE" && parts.Length > payloadIndex + 1)
        {
            name = Decode(parts[payloadIndex]);
            removeId = Decode(parts[payloadIndex + 1]);
            return true;
        }

        if ((op == "SAVE" || op == "LOAD_DATA") && parts.Length >= payloadIndex + 2)
        {
            name = Decode(parts[payloadIndex]);
            if (!int.TryParse(parts[payloadIndex + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
            {
                return false;
            }

            int index = payloadIndex + 2;
            for (int i = 0; i < count; i++)
            {
                if (!TryParsePiece(parts, index, out PlanPiece? parsed) || parsed == null)
                {
                    return false;
                }

                pieces.Add(parsed);
                index += 11;
            }

            return true;
        }

        if (op == "LOAD" && parts.Length > payloadIndex)
        {
            name = Decode(parts[payloadIndex]);
            return true;
        }

        return op == "HELLO";
    }

    private static string PieceFields(PlanPiece piece)
    {
        return string.Join("\t", PieceFieldsArray(piece));
    }

    private static string[] PieceFieldsArray(PlanPiece piece)
    {
        return new[]
        {
            Encode(piece.Id),
            Encode(piece.Prefab),
            F(piece.Position.x),
            F(piece.Position.y),
            F(piece.Position.z),
            F(piece.Rotation.x),
            F(piece.Rotation.y),
            F(piece.Rotation.z),
            F(piece.Rotation.w),
            Encode(piece.Owner),
            piece.CreatedUnixMs.ToString(CultureInfo.InvariantCulture)
        };
    }

    private static bool TryParsePiece(string[] parts, int index, out PlanPiece? piece)
    {
        piece = null;
        if (parts.Length < index + 11)
        {
            return false;
        }

        if (!TryFloat(parts[index + 2], out float px) ||
            !TryFloat(parts[index + 3], out float py) ||
            !TryFloat(parts[index + 4], out float pz) ||
            !TryFloat(parts[index + 5], out float rx) ||
            !TryFloat(parts[index + 6], out float ry) ||
            !TryFloat(parts[index + 7], out float rz) ||
            !TryFloat(parts[index + 8], out float rw) ||
            !long.TryParse(parts[index + 10], NumberStyles.Integer, CultureInfo.InvariantCulture, out long created))
        {
            return false;
        }

        piece = new PlanPiece
        {
            Id = Decode(parts[index]),
            Prefab = Decode(parts[index + 1]),
            Position = new Vector3(px, py, pz),
            Rotation = new Quaternion(rx, ry, rz, rw),
            Owner = Decode(parts[index + 9]),
            CreatedUnixMs = created
        };
        return true;
    }

    private static string F(float value)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static bool TryFloat(string value, out float result)
    {
        return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }

    private static string Encode(string value)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? ""));
    }

    private static string Decode(string value)
    {
        return Encoding.UTF8.GetString(Convert.FromBase64String(value));
    }

    private static string GetWorldKey()
    {
        try
        {
            if (ZNet.instance != null)
            {
                string name = ZNet.instance.GetWorldName();
                long uid = ZNet.instance.GetWorldUID();
                if (!string.IsNullOrWhiteSpace(name) || uid != 0L)
                {
                    return SanitizeWorldKey($"{name}_{uid.ToString(CultureInfo.InvariantCulture)}");
                }
            }
        }
        catch
        {
        }

        return "unknown";
    }

    private static string SanitizeWorldKey(string value)
    {
        char[] chars = value.Trim().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '-' && chars[i] != '_')
            {
                chars[i] = '_';
            }
        }

        string sanitized = new(chars);
        return sanitized.Length == 0 ? "unknown" : sanitized;
    }
}
