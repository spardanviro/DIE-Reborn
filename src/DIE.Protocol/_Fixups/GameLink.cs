// [DIE_Reborn FIXUP] 本文件是 GAME 树 GameProtocol\GameLink.cs 的副本。
// 唯一改动: 默认参数 `byte[] guid = /*Read out of bounds.*/` -> `byte[] guid = null`。
// ILSpy 未能还原该默认值 (原始 IL 是 [opt] 带 null 默认值), 输出了不合法的 C# (CS1525)。
// 这是客户端发送端 (GameLink : TCPMessageClient), 服务端本身用不到, 但 DIE.FakeClient 用它
// 可以获得与真实客户端字节级一致的行为, 是最强的端到端验证手段, 故保留而非排除。
// 重新反编译后需重新套用。

using System;
using StunMessage;
using StunTCP;

namespace GameProtocol;

public class GameLink : TCPMessageClient
{
	private static MessageHandler.HandleMessageDelegate<MessageCallbacks<GameLink>, GameLink> _HandleMessageMethod = HandleMessageMethod;

	private static MessageHandler.HandleRequestDelegate<MessageCallbacks<GameLink>, GameLink> _HandleRequestMethod = HandleRequestMethod;

	public int HandlePacket(ref MessageCallbacks<GameLink> callbacks, byte[] buffer, int offset, int byteCount, int messageHandleLimit = int.MaxValue)
	{
		GameLink userData = this;
		return HandlePacket(_HandleMessageMethod, _HandleRequestMethod, buffer, offset, byteCount, ref callbacks, ref userData, this, messageHandleLimit);
	}

	private static bool HandleMessageMethod(ref Message P_0, ushort P_1, ref MessageCallbacks<GameLink> P_2, ref GameLink P_3)
	{
		return ((GameMessageType)P_1).HandleMessage(ref P_2, ref P_0, ref P_3);
	}

	private static bool HandleRequestMethod(ref Message P_0, ushort P_1, ref MessageCallbacks<GameLink> P_2, ref GameLink P_3, byte[] P_4, IResponseHandler P_5)
	{
		return ((GameMessageType)P_1).HandleRequest(ref P_2, ref P_0, ref P_3, P_4, P_5);
	}

	public void GameplayData(ArraySegment<byte> P_0)
	{
		GameplayData gameplayData = new GameplayData(P_0);
		GameplayData(ref gameplayData);
	}

	public void GameplayData(ref GameplayData P_0)
	{
		Message msgBuffer = MessageHandler.GetTempMessageBuffer();
		msgBuffer.Write((ushort)38);
		MessageSerialization.Serialize(ref P_0, ref msgBuffer);
		SendMessage(ref msgBuffer);
	}

	public void GameplayAuthRequest(GameplayAuthRequest request, ResponseDelegate<GameplayAuthResponse> onResponse, double timeoutSeconds = 30.0, byte[] guid = null)
	{
		Message msg = MessageHandler.GetTempRequestBuffer(ref guid);
		msg.Write((ushort)5);
		MessageSerialization.Serialize(ref request, ref msg);
		SendRequest(delegate(RequestResult P_0, ref Message P_1)
		{
			GameplayAuthResponse response = default(GameplayAuthResponse);
			if (P_0 == RequestResult.OK && !MessageSerialization.Deserialize(ref response, ref P_1))
			{
				response = default(GameplayAuthResponse);
				P_0 = RequestResult.DeserializeFail;
			}
			onResponse(P_0, response);
		}, ref msg, guid, (int)(timeoutSeconds * 1000.0));
	}
}
