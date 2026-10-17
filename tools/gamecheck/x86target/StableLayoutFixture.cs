using System.Runtime.InteropServices;
using System.Text;

namespace ManiaReplayMaster.X86Target;

// A frozen stable x86 layout, including instruction operands and CLR strings. The gameplay and
// results objects deliberately disagree, and menu mods deliberately differ from the selected score.
internal static class StableLayoutFixture
{
    public static IntPtr Create(bool results)
    {
        var arena = Marshal.AllocHGlobal(0x3000);
        Marshal.Copy(new byte[0x3000], 0, arena, 0x3000);
        void I(int at, int value) => Marshal.WriteInt32(arena, at, value);
        void P(int at, int target) => I(at, (arena + target).ToInt32());
        void S(int at, string value)
        {
            I(at, 0x12345678); // method table, not character data
            I(at + 4, value.Length);
            var bytes = Encoding.Unicode.GetBytes(value);
            Marshal.Copy(bytes, 0, arena + at + 8, bytes.Length);
        }
        void Pattern(int at, string hex)
        {
            var bytes = hex.Split(' ').Select(b => b == "??" ? (byte)0 : Convert.ToByte(b, 16)).ToArray();
            Marshal.Copy(bytes, 0, arena + at, bytes.Length);
        }

        Pattern(0x100, "F8 01 74 04 83 65");
        P(0x100 - 0xC, 0x800); P(0x800, 0x900);
        P(0x100 - 0x33, 0x810); I(0x810, 3); I(0x818, 2);
        P(0x96C, 0x1800); S(0x1800, "d41d8cd98f00b204e9800998ecf8427e");
        P(0x978, 0x1900); S(0x1900, "123 测试曲目");
        P(0x990, 0x1A00); S(0x1A00, "测试 [4K].osu");
        I(0x9C8, 123); I(0x9CC, 456); I(0x9F8, 1000);
        I(0x930, BitConverter.SingleToInt32Bits(4));

        Pattern(0x200, "48 83 F8 04 73 1E");
        P(0x1FC, 0x820); I(0x820, results ? 7 : 2);
        Pattern(0x300, "7D 15 A1 ?? ?? ?? ?? 85 C0");
        P(0x300 - 0xB, 0x830); P(0x834, 0xA80);
        P(0xAB8, 0xC80); P(0xAE4, 0xB80); P(0xBB8, 0xD80);
        foreach (var score in new[] { 0xC80, 0xD80 })
        {
            P(score + 0x28, 0x1B00); S(0x1B00, "玩家🚀");
            P(score + 0x1C, score + 0xB0);
            I(score + 0xB8, 0x40000000); I(score + 0xBC, 0x40000000 | (score == 0xC80 ? 8 : 64));
            I(score + 0x64, 3);
            I(score + 0x68, unchecked((int)0x7FFF0146)); // only low ushort is max combo
            I(score + 0x78, score == 0xC80 ? 739977 : 123456);
            short[] hits = { 214, 3260, 9, 4315, 920, 168 };
            for (var i = 0; i < hits.Length; i++) Marshal.WriteInt16(arena, score + 0x88 + 2 * i, hits[i]);
            Marshal.WriteInt64(arena, score + 0xA0, new DateTime(2026, 10, 17, 1, 2, 3, DateTimeKind.Utc).ToBinary());
        }
        Pattern(0x400, "83 E0 20 85 C0 7E 2F");
        P(0x408, 0xF80); P(0x1038, 0x1080); P(0x1084, 0x1C00); S(0x1C00, @"D:\osu\Songs");
        Pattern(0x500, "C8 FF ?? ?? ?? ?? ?? 81 0D ?? ?? ?? ?? ?? 08 00 00");
        P(0x509, 0x840); I(0x840, 256); // menu HT, never the result's HD or gameplay's DT
        Pattern(0x600, "A1 ?? ?? ?? ?? 89 46 04 8B D6 E8");
        P(0x601, 0x850); I(0x850, 12345);
        Pattern(0x700, "55 8B EC 80 3D ?? ?? ?? ?? 00 75 26 80 3D");
        P(0x746, 0x860); Marshal.WriteByte(arena, 0x860, results ? (byte)0 : (byte)1);
        return arena;
    }
}
