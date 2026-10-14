// [DIE_Reborn FIXUP] 本文件是 GAME 树 GameProtocol\MailData.cs 的副本。
// 唯一改动: 补上缺失的 operator != 。
// 原厂 IL 里 == 和 != 必然成对存在 (C# 编译器强制), 但 ILSpy 只还原出了 ==,
// 导致 CS0216。这里按 == 的语义原样取反补回, 与原实现等价。
// 该类型只在 GetMailDataRequest/Response 里出现, 序列化由 MessageSerialization 单独处理,
// 运算符不参与任何线格式, 所以此改动对协议零影响。
// 重新反编译后需重新套用。

using ConductorCrafting;

namespace GameProtocol;

public struct MailData
{
	public string Email;

	public NewsletterFlags Newsletter;

	public byte Sex;

	public int BirthYear;

	public static bool operator ==(MailData P_0, MailData P_1)
	{
		if (P_0.Email == P_1.Email && P_0.Newsletter == P_1.Newsletter && P_0.Sex == P_1.Sex)
		{
			return P_0.BirthYear == P_1.BirthYear;
		}
		return false;
	}

	public static bool operator !=(MailData P_0, MailData P_1)
	{
		return !(P_0 == P_1);
	}

	public override bool Equals(object P_0)
	{
		if (P_0 is MailData)
		{
			return (MailData)P_0 == this;
		}
		return false;
	}

	public override int GetHashCode()
	{
		return base.GetHashCode();
	}
}
