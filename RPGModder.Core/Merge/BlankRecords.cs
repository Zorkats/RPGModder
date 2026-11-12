using Newtonsoft.Json.Linq;
using RPGModder.Core.Services;

namespace RPGModder.Core.Merge;

/// <summary>
/// The record the editor writes when a database maximum is raised (GR-R8), with <c>id</c> 0. The MZ shapes are the
/// records the MZ 1.10.0 editor wrote into a default project; the MV shapes are the MZ ones without the fields MV's
/// data format (<c>json_docs.js</c> in the MV core script) does not list, and MV's own animation fields. An unknown
/// engine gets the MV shapes, which both engines read.
/// </summary>
internal static class BlankRecords
{
    /// <summary>A field of skills and states only the MZ editor writes; the game never reads it.</summary>
    private const string MessageTypeProperty = "messageType";

    private const string ParamsProperty = "params";

    private static readonly Dictionary<string, string> MzShapes = new(StringComparer.Ordinal)
    {
        ["actors.json"] =
            """{"id":0,"battlerName":"","characterIndex":0,"characterName":"","classId":1,"equips":[0,0,0,0,0],"faceIndex":0,"faceName":"","traits":[],"initialLevel":1,"maxLevel":99,"name":"","nickname":"","note":"","profile":""}""",
        ["classes.json"] =
            """{"id":0,"expParams":[30,20,30,30],"traits":[{"code":23,"dataId":0,"value":1},{"code":22,"dataId":0,"value":0.95},{"code":22,"dataId":1,"value":0.05},{"code":22,"dataId":2,"value":0.04},{"code":41,"dataId":1,"value":0},{"code":51,"dataId":1,"value":0},{"code":52,"dataId":1,"value":0}],"learnings":[],"name":"","note":"","params":[]}""",
        ["skills.json"] =
            """{"id":0,"animationId":0,"damage":{"critical":false,"elementId":0,"formula":"0","type":0,"variance":20},"description":"","effects":[],"hitType":0,"iconIndex":0,"message1":"","message2":"","mpCost":0,"name":"","note":"","occasion":0,"repeats":1,"requiredWtypeId1":0,"requiredWtypeId2":0,"scope":1,"speed":0,"stypeId":1,"successRate":100,"tpCost":0,"tpGain":0,"messageType":1}""",
        ["items.json"] =
            """{"id":0,"animationId":0,"consumable":true,"damage":{"critical":false,"elementId":0,"formula":"0","type":0,"variance":20},"description":"","effects":[],"hitType":0,"iconIndex":0,"itypeId":1,"name":"","note":"","occasion":0,"price":0,"repeats":1,"scope":7,"speed":0,"successRate":100,"tpGain":0}""",
        ["weapons.json"] =
            """{"id":0,"animationId":0,"description":"","etypeId":1,"traits":[{"code":31,"dataId":1,"value":0},{"code":22,"dataId":0,"value":0}],"iconIndex":0,"name":"","note":"","params":[0,0,0,0,0,0,0,0],"price":0,"wtypeId":0}""",
        ["armors.json"] =
            """{"id":0,"atypeId":0,"description":"","etypeId":2,"traits":[{"code":22,"dataId":1,"value":0}],"iconIndex":0,"name":"","note":"","params":[0,0,0,0,0,0,0,0],"price":0}""",
        ["enemies.json"] =
            """{"id":0,"actions":[{"conditionParam1":0,"conditionParam2":0,"conditionType":0,"rating":5,"skillId":1}],"battlerHue":0,"battlerName":"","dropItems":[{"dataId":1,"denominator":1,"kind":0},{"dataId":1,"denominator":1,"kind":0},{"dataId":1,"denominator":1,"kind":0}],"exp":0,"traits":[{"code":22,"dataId":0,"value":0.95},{"code":22,"dataId":1,"value":0.05},{"code":31,"dataId":1,"value":0}],"gold":0,"name":"","note":"","params":[100,0,10,10,10,10,10,10]}""",
        ["troops.json"] =
            """{"id":0,"members":[],"name":"","pages":[{"conditions":{"actorHp":50,"actorId":1,"actorValid":false,"enemyHp":50,"enemyIndex":0,"enemyValid":false,"switchId":1,"switchValid":false,"turnA":0,"turnB":0,"turnEnding":false,"turnValid":false},"list":[{"code":0,"indent":0,"parameters":[]}],"span":0}]}""",
        ["states.json"] =
            """{"id":0,"autoRemovalTiming":0,"chanceByDamage":100,"iconIndex":0,"maxTurns":1,"message1":"","message2":"","message3":"","message4":"","minTurns":1,"motion":0,"name":"","note":"","overlay":0,"priority":50,"removeAtBattleEnd":false,"removeByDamage":false,"removeByRestriction":false,"removeByWalking":false,"restriction":0,"stepsToRemove":100,"traits":[],"messageType":1}""",
        ["animations.json"] =
            """{"id":0,"displayType":0,"effectName":"","flashTimings":[],"name":"","offsetX":0,"offsetY":0,"rotation":{"x":0,"y":0,"z":0},"scale":100,"soundTimings":[],"speed":100,"alignBottom":false}""",
        ["tilesets.json"] =
            """{"id":0,"flags":[16],"mode":1,"name":"","note":"","tilesetNames":["","","","","","","","",""]}""",
        ["commonevents.json"] =
            """{"id":0,"list":[{"code":0,"indent":0,"parameters":[]}],"name":"","switchId":1,"trigger":0}"""
    };

    /// <summary>
    /// The MV animation format: two sprite sheets, a frame list and timings. One empty frame, so the game can play
    /// the blank without reading past the list; position 1 is the center. Not pinned against an MV editor.
    /// </summary>
    private const string MvAnimation =
        """{"id":0,"animation1Hue":0,"animation1Name":"","animation2Hue":0,"animation2Name":"","frames":[[]],"name":"","position":1,"timings":[]}""";

    /// <summary>The eight parameter curves of a blank class, levels 0 to 99, as the MZ editor writes them.</summary>
    private static readonly int[][] ClassCurves =
    [
        [
            1, 450, 500, 550, 600, 650, 700, 750, 800, 850, 900, 950, 1000, 1050, 1100, 1150, 1200, 1250, 1300, 1350,
            1400, 1450, 1500, 1550, 1600, 1650, 1700, 1750, 1800, 1850, 1900, 1950, 2000, 2050, 2100, 2150, 2200, 2250, 2300, 2350,
            2400, 2450, 2500, 2550, 2600, 2650, 2700, 2750, 2800, 2850, 2900, 2950, 3000, 3050, 3100, 3150, 3200, 3250, 3300, 3350,
            3400, 3450, 3500, 3550, 3600, 3650, 3700, 3750, 3800, 3850, 3900, 3950, 4000, 4050, 4100, 4150, 4200, 4250, 4300, 4350,
            4400, 4450, 4500, 4550, 4600, 4650, 4700, 4750, 4800, 4850, 4900, 4950, 5000, 5050, 5100, 5150, 5200, 5250, 5300, 5350
        ],
        [
            0, 90, 100, 110, 120, 130, 140, 150, 160, 170, 180, 190, 200, 210, 220, 230, 240, 250, 260, 270,
            280, 290, 300, 310, 320, 330, 340, 350, 360, 370, 380, 390, 400, 410, 420, 430, 440, 450, 460, 470,
            480, 490, 500, 510, 520, 530, 540, 550, 560, 570, 580, 590, 600, 610, 620, 630, 640, 650, 660, 670,
            680, 690, 700, 710, 720, 730, 740, 750, 760, 770, 780, 790, 800, 810, 820, 830, 840, 850, 860, 870,
            880, 890, 900, 910, 920, 930, 940, 950, 960, 970, 980, 990, 1000, 1010, 1020, 1030, 1040, 1050, 1060, 1070
        ],
        [
            1, 16, 18, 19, 20, 21, 23, 24, 25, 26, 28, 29, 30, 31, 33, 34, 35, 36, 38, 39,
            40, 41, 43, 44, 45, 46, 48, 49, 50, 51, 53, 54, 55, 56, 58, 59, 60, 61, 63, 64,
            65, 66, 68, 69, 70, 71, 73, 74, 75, 76, 77, 79, 80, 81, 82, 84, 85, 86, 87, 89,
            90, 91, 92, 94, 95, 96, 97, 99, 100, 101, 102, 104, 105, 106, 107, 109, 110, 111, 112, 114,
            115, 116, 117, 119, 120, 121, 122, 124, 125, 126, 127, 129, 130, 131, 132, 134, 135, 136, 137, 138
        ],
        [
            0, 16, 18, 19, 20, 21, 23, 24, 25, 26, 28, 29, 30, 31, 33, 34, 35, 36, 38, 39,
            40, 41, 43, 44, 45, 46, 48, 49, 50, 51, 53, 54, 55, 56, 58, 59, 60, 61, 63, 64,
            65, 66, 68, 69, 70, 71, 73, 74, 75, 76, 77, 79, 80, 81, 82, 84, 85, 86, 87, 89,
            90, 91, 92, 94, 95, 96, 97, 99, 100, 101, 102, 104, 105, 106, 107, 109, 110, 111, 112, 114,
            115, 116, 117, 119, 120, 121, 122, 124, 125, 126, 127, 129, 130, 131, 132, 134, 135, 136, 137, 138
        ],
        [
            0, 16, 18, 19, 20, 21, 23, 24, 25, 26, 28, 29, 30, 31, 33, 34, 35, 36, 38, 39,
            40, 41, 43, 44, 45, 46, 48, 49, 50, 51, 53, 54, 55, 56, 58, 59, 60, 61, 63, 64,
            65, 66, 68, 69, 70, 71, 73, 74, 75, 76, 77, 79, 80, 81, 82, 84, 85, 86, 87, 89,
            90, 91, 92, 94, 95, 96, 97, 99, 100, 101, 102, 104, 105, 106, 107, 109, 110, 111, 112, 114,
            115, 116, 117, 119, 120, 121, 122, 124, 125, 126, 127, 129, 130, 131, 132, 134, 135, 136, 137, 138
        ],
        [
            0, 16, 18, 19, 20, 21, 23, 24, 25, 26, 28, 29, 30, 31, 33, 34, 35, 36, 38, 39,
            40, 41, 43, 44, 45, 46, 48, 49, 50, 51, 53, 54, 55, 56, 58, 59, 60, 61, 63, 64,
            65, 66, 68, 69, 70, 71, 73, 74, 75, 76, 77, 79, 80, 81, 82, 84, 85, 86, 87, 89,
            90, 91, 92, 94, 95, 96, 97, 99, 100, 101, 102, 104, 105, 106, 107, 109, 110, 111, 112, 114,
            115, 116, 117, 119, 120, 121, 122, 124, 125, 126, 127, 129, 130, 131, 132, 134, 135, 136, 137, 138
        ],
        [
            0, 32, 35, 37, 40, 42, 45, 47, 50, 52, 55, 57, 60, 62, 65, 67, 70, 72, 75, 77,
            80, 82, 85, 87, 90, 92, 95, 97, 100, 102, 105, 107, 110, 112, 115, 117, 120, 122, 125, 127,
            130, 132, 135, 137, 140, 142, 145, 147, 150, 152, 155, 157, 160, 162, 165, 167, 170, 172, 175, 177,
            180, 182, 185, 187, 190, 192, 195, 197, 200, 202, 205, 207, 210, 212, 215, 217, 220, 222, 225, 227,
            230, 232, 235, 237, 240, 242, 245, 247, 250, 252, 255, 257, 260, 262, 265, 267, 270, 272, 275, 277
        ],
        [
            0, 32, 35, 37, 40, 42, 45, 47, 50, 52, 55, 57, 60, 62, 65, 67, 70, 72, 75, 77,
            80, 82, 85, 87, 90, 92, 95, 97, 100, 102, 105, 107, 110, 112, 115, 117, 120, 122, 125, 127,
            130, 132, 135, 137, 140, 142, 145, 147, 150, 152, 155, 157, 160, 162, 165, 167, 170, 172, 175, 177,
            180, 182, 185, 187, 190, 192, 195, 197, 200, 202, 205, 207, 210, 212, 215, 217, 220, 222, 225, 227,
            230, 232, 235, 237, 240, 242, 245, 247, 250, 252, 255, 257, 260, 262, 265, 267, 270, 272, 275, 277
        ]
    ];

    /// <summary>A new copy of the blank record of <paramref name="fileName"/>, or <c>null</c> for a file that is not a database file.</summary>
    public static JObject? For(string fileName, RpgMakerEngine? engine)
    {
        if (!MzShapes.TryGetValue(fileName, out string? text))
        {
            return null;
        }

        bool mz = engine == RpgMakerEngine.MZ;
        if (!mz && fileName == "animations.json")
        {
            return JObject.Parse(MvAnimation);
        }

        var record = JObject.Parse(text);
        if (fileName == "classes.json")
        {
            record[ParamsProperty] = new JArray(ClassCurves.Select(curve => new JArray(curve)));
        }

        if (!mz)
        {
            record.Remove(MessageTypeProperty);
        }

        return record;
    }
}
