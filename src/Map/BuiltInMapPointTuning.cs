using System;

namespace ai02;

public static class BuiltInMapPointTuning
{
    public static void Apply(FrontlineMapType mapType, MapAnnotationPoint point)
    {
        if (point == null || string.IsNullOrWhiteSpace(point.Label))
            return;

        switch (mapType)
        {
            case FrontlineMapType.BorderlandRuinsSecure:
                ApplyBorderland(point);
                break;
            case FrontlineMapType.SealRock:
                ApplySealRock(point);
                break;
            case FrontlineMapType.FieldsOfHonor:
                ApplyFieldsOfHonor(point);
                break;
            case FrontlineMapType.OnsalHakair:
                ApplyOnsal(point);
                break;
            case FrontlineMapType.Vochester:
                ApplyVochester(point);
                break;
        }
    }

    private static void ApplyBorderland(MapAnnotationPoint point)
    {
        if (LabelEquals(point, "跳下去会死"))
        {
            Set(point, "断崖坠落区", 95);
            return;
        }

        if (LabelEquals(point, "南崖") || LabelEquals(point, "东崖") || LabelEquals(point, "西崖"))
        {
            Set(point, $"{point.Label}断崖口", 88);
            return;
        }

        if (LabelStartsWith(point, "传送装置入口"))
        {
            Set(point, point.Label.Replace("传送装置入口", "传送装置入口卡位", StringComparison.Ordinal), 66);
            return;
        }

        if (LabelStartsWith(point, "传送装置出口"))
        {
            Set(point, point.Label.Replace("传送装置出口", "传送装置出口落点", StringComparison.Ordinal), 62);
            return;
        }

        if (LabelStartsWith(point, "很高的桥"))
        {
            Set(point, point.Label.Replace("很高的桥", "高桥火力位", StringComparison.Ordinal), 72);
            return;
        }

        if (point.Kind == MapAnnotationKind.HighGround && LabelEquals(point, "高台"))
        {
            Set(point, "怪物高台", 38);
            return;
        }

        if (point.Kind == MapAnnotationKind.LowGround && LabelEquals(point, "低地"))
        {
            Set(point, "怪物低地", 64);
            return;
        }

        if (LabelEquals(point, "窄口"))
            Set(point, "传送前窄口", 66);
    }

    private static void ApplySealRock(MapAnnotationPoint point)
    {
        if (LabelEquals(point, "洞"))
        {
            Set(point, "洞内窄口", 84);
            return;
        }

        if (LabelEquals(point, "桥口"))
        {
            Set(point, "主桥口", 86);
            return;
        }

        if (LabelEquals(point, "地形优势口"))
        {
            Set(point, "高低差卡口", 78);
            return;
        }

        if (LabelEquals(point, "洞门口高台"))
        {
            Set(point, "洞门口高台火力位", point.Kind == MapAnnotationKind.HighGround ? 46 : 60);
            return;
        }

        if (LabelEquals(point, "海边"))
        {
            if (point.Kind == MapAnnotationKind.LowGround)
            {
                Set(point, "海边低地", 72);
                return;
            }

            if (point.Kind == MapAnnotationKind.HighGround)
            {
                Set(point, "海边高台", 44);
                return;
            }

            if (point.Kind == MapAnnotationKind.Bridge)
            {
                Set(point, "海边桥", 78);
                return;
            }
        }

        if (point.Kind == MapAnnotationKind.HighGround
            && (LabelEquals(point, "海边高台") || LabelEquals(point, "c2高台") || LabelEquals(point, "c4高台") || LabelEquals(point, "d3高台")))
        {
            Set(point, $"{point.Label}火力位", 44);
        }
    }

    private static void ApplyFieldsOfHonor(MapAnnotationPoint point)
    {
        if (LabelEquals(point, "中央窄口"))
        {
            Set(point, "中央窄口爆点", 84);
            return;
        }

        if (LabelEquals(point, "冰口"))
        {
            Set(point, "冰口爆点", 76);
            return;
        }

        if (LabelEquals(point, "窄桥"))
        {
            Set(point, "窄桥爆点", 82);
            return;
        }

        if (LabelEquals(point, "桥洞"))
        {
            Set(point, "桥洞回接位", 68);
            return;
        }

        if (point.Kind == MapAnnotationKind.HighGround && LabelEquals(point, "高台"))
        {
            Set(point, "桥上火力位", 44);
            return;
        }

        if (point.Kind == MapAnnotationKind.LowGround && LabelEquals(point, "低地"))
            Set(point, "大冰坑低地", 72);
    }

    private static void ApplyOnsal(MapAnnotationPoint point)
    {
        if (LabelEquals(point, "桥口子"))
        {
            Set(point, "中心桥口", 84);
            return;
        }

        if (LabelEquals(point, "天桥窄路口"))
        {
            Set(point, "天桥窄路口", 88);
            return;
        }

        if (LabelEquals(point, "桥下窄口"))
        {
            Set(point, "桥下窄口", 78);
            return;
        }

        if (LabelEquals(point, "窄口"))
        {
            Set(point, "侧线窄口", 74);
            return;
        }

        if (LabelEquals(point, "01桥"))
        {
            Set(point, "01主桥", 76);
            return;
        }

        if (LabelEquals(point, "07桥"))
        {
            Set(point, "07侧桥", 72);
            return;
        }

        if (LabelEquals(point, "洞"))
        {
            Set(point, "桥下洞口", 70);
            return;
        }

        if (point.Kind == MapAnnotationKind.HighGround && LabelEquals(point, "高台"))
        {
            Set(point, "桥侧高台", 42);
            return;
        }

        if (LabelEquals(point, "03高台") || LabelEquals(point, "04高台") || LabelEquals(point, "05高台"))
        {
            Set(point, $"{point.Label}火力位", 40);
            return;
        }

        if (LabelEquals(point, "下家门口高台"))
            Set(point, "下家门口高台", 38);
    }

    private static void ApplyVochester(MapAnnotationPoint point)
    {
        if (LabelEquals(point, "中央低地"))
        {
            Set(point, "中央低地爆点", 82);
            return;
        }

        if (LabelEquals(point, "11洞内区域"))
        {
            Set(point, "11洞内低地陷阱", 86);
            return;
        }

        if (LabelEquals(point, "10高台"))
        {
            Set(point, "10高台火力位", 50);
            return;
        }

        if (LabelEquals(point, "11点高台"))
        {
            Set(point, "11点高台火力位", 52);
            return;
        }

        if (LabelEquals(point, "12高台"))
        {
            Set(point, point.Kind == MapAnnotationKind.HighGround ? "12高台火力位" : "12高台连桥", point.Kind == MapAnnotationKind.HighGround ? 50 : 64);
            return;
        }

        if (LabelEquals(point, "上坡下坡口"))
        {
            Set(point, "中央坡口", 72);
            return;
        }

        if (LabelEquals(point, "洞口"))
        {
            Set(point, "11洞口", 76);
            return;
        }

        if (LabelEquals(point, "11洞边高地小路"))
        {
            Set(point, "11洞边高地连桥", 62);
            return;
        }

        if (LabelEquals(point, "10高台点边缘"))
        {
            Set(point, "10高台边缘火力位", 46);
            return;
        }

        if (LabelEquals(point, "10高台点北桥"))
        {
            Set(point, "10高台北桥连线", 64);
            return;
        }

        if (LabelEquals(point, "10高台点南桥"))
        {
            Set(point, "10高台南桥连线", 64);
            return;
        }

        if (LabelEquals(point, "点12高台东"))
        {
            Set(point, "12高台东侧火力线", 56);
            return;
        }

        if (LabelEquals(point, "点12高台西南"))
            Set(point, "12高台西南回接线", 52);
    }

    private static void Set(MapAnnotationPoint point, string label, int riskScore)
    {
        point.Label = label;
        point.RiskScore = Math.Clamp(riskScore, 0, 100);
    }

    private static bool LabelEquals(MapAnnotationPoint point, string label)
        => string.Equals(point.Label, label, StringComparison.Ordinal);

    private static bool LabelStartsWith(MapAnnotationPoint point, string prefix)
        => point.Label.StartsWith(prefix, StringComparison.Ordinal);
}
