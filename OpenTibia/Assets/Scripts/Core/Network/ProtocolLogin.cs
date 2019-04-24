﻿using System.Collections.Generic;
using System.Net.Sockets;
using UnityEngine.Events;

namespace OpenTibiaUnity.Core.Network
{
    public class ProtocolLogin : Protocol
    {
        public const string ErrorMsg_10061_HostUnreachable = "Cannot connect to a login server.\n\nError(10061): Connection refused.\n\nAll login servers are offline. Check www.opentibiaunity.com for more information.\n\nFor more information take a look at the FAQS in the support section at www.opentibiaunity.com";

        public class LoginErrorEvent : UnityEvent<string> { };
        public class LoginTokenErrorEvent : UnityEvent<int> { };
        public class MotdEvent : UnityEvent<int, string> { };
        public class SessionKeyEvent : UnityEvent<string> { };
        public class CharacterListEvent : UnityEvent<CharacterList> { };

        public string AccountName { get; set; }
        public string Password { get; set; }

        public LoginErrorEvent onCustomLoginError = new LoginErrorEvent();
        public LoginErrorEvent onLoginError = new LoginErrorEvent();
        public LoginTokenErrorEvent onLoginTokenError = new LoginTokenErrorEvent();
        public MotdEvent onMotd = new MotdEvent();
        public SessionKeyEvent onSessionKey = new SessionKeyEvent();
        public CharacterListEvent onCharacterList = new CharacterListEvent();
        public UnityEvent onUpdateRequired = new UnityEvent();

        private byte m_PrevOpcode = 0;
        private byte m_LastOpcode = 0;

        public override void Connect() {
            throw new System.InvalidOperationException("You can't connect without ip/port in a plogin.");
        }

        protected override void OnConnect() {
            try {
                SendLogin();
            } catch (System.Exception e) {
                onLoginError.Invoke(string.Format("Internal Error: {0}", e.Message));
                Disconnect();
            }

            BeginRecv();
        }
        protected override void OnError(SocketError code, string message) {
            if (code == SocketError.ConnectionRefused) {
                // host is not listening on this port / host refused the connection
                onCustomLoginError.Invoke(ErrorMsg_10061_HostUnreachable);
            } else if (code == SocketError.HostUnreachable) {
                // no internet connection / host is not online
                onLoginError.Invoke("The host is currently unreachable.");
            } else {
                // other sort of errors
                onLoginError.Invoke(string.Format("Error({0}): {1}", code, message));
            }
        }
        protected override void OnRecv(InputMessage message) {
            base.OnRecv(message);
            OpenTibiaUnity.GameManager.InvokeOnMainThread(() => {
                while (message.CanRead(1)) {
                    byte opcode = message.GetU8();
                    switch (opcode) {
                        case LoginServerOpCodes.LoginError:
                        case LoginServerOpCodes.Login_1076_Error:
                            string error = message.GetString();
                            onLoginError.Invoke(error);
                            break;

                        case LoginServerOpCodes.LoginTokenSuccess:
                            message.GetU8(); // Ok...
                            break;

                        case LoginServerOpCodes.LoginTokenError:
                            byte unknown = message.GetU8();
                            onLoginTokenError.Invoke(unknown);
                            break;

                        case LoginServerOpCodes.LoginMotd:
                            string[] motdinfo = message.GetString().Split('\n');
                            if (motdinfo.Length == 2) {
                                int number;
                                int.TryParse(motdinfo[0], out number);
                                onMotd.Invoke(number, motdinfo[1]);
                            }
                            break;

                        case LoginServerOpCodes.UpdateRequired:
                            onUpdateRequired.Invoke();
                            break;

                        case LoginServerOpCodes.LoginSessionKey:
                            string sessionKey = message.GetString();
                            onSessionKey.Invoke(sessionKey);
                            break;

                        case LoginServerOpCodes.LoginCharacterList:
                            CharacterList characterList = new CharacterList(message);
                            onCharacterList.Invoke(characterList);
                            break;

                        default:
                            string err = string.Format("<ProtocolGame> Unknown opcode received ({0}), last opcode ({1}), prev opcode ({2}), unread ({3})", opcode, m_LastOpcode, m_PrevOpcode, message.GetUnreadSize());
                            throw new System.Exception(err);
                    }

                    m_PrevOpcode = m_LastOpcode;
                    m_LastOpcode = opcode;
                }
            });
        }
        
        protected void SendLogin() {
            OutputMessage message = new OutputMessage();
            message.AddU8(LoginClientOpCodes.EnterAccount);
            message.AddU16(Utility.OperatingSystem.GetCurrentOs());

            var gameManager = OpenTibiaUnity.GameManager;

            message.AddU16((ushort)gameManager.ProtocolVersion);
            if (gameManager.GetFeature(GameFeatures.GameClientVersion))
                message.AddU32((uint)gameManager.ClientVersion);

            if (gameManager.GetFeature(GameFeatures.GameContentRevision)) {
                message.AddU16(Constants.ContentRevision);
                message.AddU16(0);
            } else {
                message.AddU32(Constants.DatSignature);
            }

            message.AddU32(Constants.SprSignature);
            message.AddU32(Constants.PicSignature);

            if (gameManager.GetFeature(GameFeatures.GamePreviewState))
                message.AddU8(0x00);

            int offset = message.Tell();
            var random = new System.Random();
            if (gameManager.GetFeature(GameFeatures.GameLoginPacketEncryption)) {
                message.AddU8(0); // first byte must be zero

                GenerateXteaKey(random);
                AddXteaKey(message);
            }

            if (gameManager.GetFeature(GameFeatures.GameAccountNames))
                message.AddString(AccountName);
            else
                message.AddU32(uint.Parse(AccountName));
            
            message.AddString(Password);

            int paddingBytes = Crypto.RSA.GetRsaSize() - (message.Tell() - offset);
            for (int i = 0; i < paddingBytes; i++) {
                message.AddU8((byte)random.Next(0xFF));
            }
            
            if (gameManager.GetFeature(GameFeatures.GameLoginPacketEncryption))
                Crypto.RSA.EncryptMessage(message);

            if (gameManager.GetFeature(GameFeatures.GameOGLInformation)) {
                message.AddU8(1);
                message.AddU8(1);

                if (gameManager.ClientVersion >= 1072)
                    message.AddString(string.Format("{0} {1}", OpenTibiaUnity.GraphicsVendor, OpenTibiaUnity.GraphicsDevice));
                else
                    message.AddString(OpenTibiaUnity.GraphicsDevice);

                message.AddString(OpenTibiaUnity.GraphicsVersion);
            }

            if (gameManager.GetFeature(GameFeatures.GameAuthenticator)) {
                offset = message.Tell();

                message.AddU8(0);
                message.AddString(string.Empty); // no auth-token

                message.AddU8(0); // stay logged-in for a while

                paddingBytes = Crypto.RSA.GetRsaSize() - (message.Tell() - offset);
                for (int i = 0; i < paddingBytes; i++) {
                    message.AddU8((byte)random.Next(0xFF));
                }

                Crypto.RSA.EncryptMessage(message);
            }

            if (gameManager.GetFeature(GameFeatures.GameProtocolChecksum))
                ChecksumEnabled = true;
            
            WriteToOutput(message);

            if (gameManager.GetFeature(GameFeatures.GameLoginPacketEncryption))
                XteaEnabled = true;
        }
    }

    public class CharacterList
    {
        public CharacterList(InputMessage message) {
            Parse(message);
        }

        public List<World> Worlds { get; private set; } = new List<World>();
        public List<Character> Characters { get; private set; } = new List<Character>();

        public int AccountState { get; private set; }
        public bool IsPremium { get; private set; }
        public bool InfinitePremium { get; private set; }
        
        public ushort PremiumDays { get; private set; }
        public uint PremiumTimeStamp { get; private set; }

        public struct World
        {
            public int ID;
            public string Name;
            public string HostName;
            public int Port;
            public bool Preview;
        }

        public struct Character
        {
            public int World;
            public string Name;
        }
        
        public void Parse(InputMessage message) {
            if (OpenTibiaUnity.GameManager.ClientVersion >= 1010) {
                byte worlds = message.GetU8();
                for (int i = 0; i < worlds; i++) {
                    var world = new World();
                    world.ID = message.GetU8();
                    world.Name = message.GetString();
                    world.HostName = message.GetString();
                    world.Port = message.GetU16();
                    world.Preview = message.GetBool();
                    Worlds.Add(world);
                }

                byte characters = message.GetU8();
                for (int i = 0; i < characters; i++) {
                    Character character = new Character();
                    character.World = message.GetU8();
                    character.Name = message.GetString();
                    Characters.Add(character);
                }
            } else {
                byte characters = message.GetU8();
                for (int i = 0; i < characters; i++) {
                    Character character = new Character();
                    character.Name = message.GetString();

                    var worldName = message.GetString();
                    var worldIpLong = message.GetU32();
                    var worldPort = message.GetU16();

                    World world = GetOrCreateWorld(worldName, worldIpLong, worldPort);
                    character.World = world.ID;
                    Characters.Add(character);
                }
            }

            uint now = (uint)System.DateTime.Now.Second;
            if (OpenTibiaUnity.GameManager.ClientVersion >= 1077) {
                AccountState = message.GetU8();
                IsPremium = message.GetBool();
                PremiumTimeStamp = message.GetU32();
                if (PremiumTimeStamp > now)
                    PremiumDays = (ushort)((PremiumTimeStamp - now) / 86400U);
                else
                    PremiumDays = 0;

                InfinitePremium = (IsPremium && PremiumTimeStamp == 0);
            } else {
                AccountState = 0;
                PremiumDays = message.GetU16();
                if (PremiumDays > 0)
                    PremiumTimeStamp = now + PremiumDays * 86400U;
                else
                    PremiumTimeStamp = 0;

                IsPremium = PremiumDays > 0;
                InfinitePremium = PremiumDays == 65535;
            }
        }

        protected World GetOrCreateWorld(string name, uint ip, ushort port) {
            string ipAddress = new System.Net.IPAddress(ip).ToString();
            foreach (var world in Worlds) {
                if (world.Name == name && world.HostName == ipAddress && world.Port == port)
                    return world;
            }

            World newWorld = new World();
            newWorld.ID = Worlds.Count;
            newWorld.Name = name;
            newWorld.HostName = ipAddress;
            newWorld.Port = port;
            newWorld.Preview = false;

            Worlds.Add(newWorld);
            return newWorld;
        }

        public World FindWorld(int id) {
            return Worlds.Find((World x) => {
                return x.ID == id;
            });
        }
        public World FindWorld(string name) {
            return Worlds.Find((World x) => {
                return x.Name == name;
            });
        }
    }
}