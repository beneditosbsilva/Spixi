﻿using IXICore;
using IXICore.Meta;
using IXICore.Network;
using SPIXI.Meta;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SPIXI
{
    public enum FriendMessageType
    {
        standard,
        requestAdd,
        requestFunds,
        sentFunds
    }


    public class FriendMessage
    {
        public string message;
        public string timestamp;
        public bool from;
        public bool read;
        public FriendMessageType type;

        public FriendMessage(string msg, string time, bool fr)
        {
            message = msg;
            timestamp = time;
            from = fr;
            read = false;
            type = FriendMessageType.standard;
        }

        public FriendMessage(string msg, string time, bool fr, FriendMessageType t)
        {
            message = msg;
            timestamp = time;
            from = fr;
            read = false;
            type = t;
        }
    }


    public class Friend
    {
        public byte[] walletAddress;
        public byte[] publicKey;

        public string nickname
        {
            get;
            set;
        }

        public byte[] chachaKey = null; // TODO TODO don't keep keys in plaintext in memory
        public byte[] aesKey = null; // TODO TODO don't keep keys in plaintext in memory
        public long keyGeneratedTime = 0;

        public string relayIP = null;
        public byte[] relayWallet = null;

        public bool online = false;

        public List<FriendMessage> messages = new List<FriendMessage>();

        public SingleChatPage chat_page = null;

        public bool approved = true;

        public Friend(byte[] wallet, byte[] public_key, string nick, byte[] aes_key, byte[] chacha_key, long key_generated_time, bool approve = true)
        {
            walletAddress = wallet;
            publicKey = public_key;
            nickname = nick;
            approved = approve;

            chachaKey = chacha_key;
            aesKey = aes_key;
            keyGeneratedTime = key_generated_time;

            // Read messages from chat history
            messages = Node.localStorage.readMessagesFile(wallet);
        }

        // Get the number of unread messages
        // TODO: optimize this
        public int getUnreadMessageCount()
        {
            int unreadCount = 0;
            foreach(FriendMessage message in messages)
            {
                if(message.read == false)
                {
                    unreadCount++;
                }
            }
            return unreadCount;
        }

        // Flushes the temporary message history
        public bool flushHistory()
        {
            messages.Clear();
            return true;
        }

        // Deletes the history file and flushes the temporary history
        public bool deleteHistory()
        {

            if (Node.localStorage.deleteMessagesFile(walletAddress) == false)
                return false;

            if (flushHistory() == false)
                return false;

            return true;
        }

        // Check if the last message is unread. Returns true if it is unread.
        public bool checkLastUnread()
        {
            if (messages.Count < 1)
                return false;
            FriendMessage last_message = messages[messages.Count - 1];
            if (last_message.read == false)
                return true;

            return false;
        }

        public int getMessageCount()
        {
            return messages.Count;
        }

        // Set last message as read
        public void setLastRead()
        {
            if (messages.Count < 1)
                return;
            FriendMessage last_message = messages[messages.Count - 1];
            last_message.read = true;
        }


        // Generates a random chacha key and a random aes key
        // Returns the two keys encrypted using the supplied public key
        // Returns false if not enough time has passed to generate the keys
        public bool generateKeys()
        {
            // TODO TODO TODO keys should be re-generated periodically
            try
            {
                if (aesKey == null)
                {
                    aesKey = CryptoManager.lib.getSecureRandomBytes(32);
                    return true;
                }

                if (chachaKey == null)
                {
                    chachaKey = CryptoManager.lib.getSecureRandomBytes(32);
                    return true;
                }
            }
            catch (Exception e)
            {
                Logging.error(String.Format("Exception during generate keys: {0}", e.Message));
            }

            return false;
        }

        public bool sendKeys(int selected_key)
        {
            try
            {
                using (MemoryStream m = new MemoryStream())
                {
                    using (BinaryWriter writer = new BinaryWriter(m))
                    {
                        if (aesKey != null && selected_key != 2)
                        {
                            writer.Write(aesKey.Length);
                            writer.Write(aesKey);
                            Logging.info("Sending aes key");
                        }else
                        {
                            writer.Write(0);
                        }

                        if (chachaKey != null && selected_key != 1)
                        {
                            writer.Write(chachaKey.Length);
                            writer.Write(chachaKey);
                            Logging.info("Sending chacha key");
                        }
                        else
                        {
                            writer.Write(0);
                        }

                        Logging.info("Preparing key message");

                        SpixiMessage spixi_message = new SpixiMessage(SpixiMessageCode.keys, m.ToArray());

                        // Send the nickname message to the S2 nodes
                        StreamMessage sm = new StreamMessage();
                        sm.type = StreamMessageCode.info;
                        sm.recipient = walletAddress;
                        sm.sender = Node.walletStorage.getPrimaryAddress();
                        sm.transaction = new byte[1];
                        sm.sigdata = new byte[1];
                        sm.data = spixi_message.getBytes();
                        sm.encryptionType = StreamMessageEncryptionCode.rsa;

                        StreamProcessor.sendMessage(this, sm, false);
                    }
                }
                return true;
            }
            catch (Exception e)
            {
                Logging.error(String.Format("Exception during send keys: {0}", e.Message));
            }

            return false;
        }

        // Handles receiving and decryption of keys
        public bool receiveKeys(byte[] data)
        {
            try
            {
                Logging.info("Received keys");
                byte[] decrypted = data;

                using (MemoryStream m = new MemoryStream(data))
                {
                    using (BinaryReader reader = new BinaryReader(m))
                    {
                        // Read and assign the aes password
                        int aes_length = reader.ReadInt32();
                        byte[] aes = reader.ReadBytes(aes_length);

                        // Read the chacha key
                        int cc_length = reader.ReadInt32();
                        byte[] chacha = reader.ReadBytes(cc_length);

                        if (aesKey == null)
                        {
                            aesKey = aes;
                        }

                        if (chachaKey == null)
                        {
                            chachaKey = chacha;
                        }

                        // Everything succeeded
                        return true;
                    }
                }
            }
            catch (Exception e)
            {
                Logging.error(String.Format("Exception during receive keys: {0}", e.Message));
            }

            return false;
        }

        // Retrieve the friend's connected S2 node address. Returns null if not found
        public string searchForRelay()
        {
            relayIP = null;
            relayWallet = null;

            string hostname = FriendList.getRelayHostname(walletAddress);

            if (hostname != null)
            {
                // Store the last relay ip and wallet for this friend
                relayIP = hostname;
            }
            // Finally, return the ip address of the node
            return relayIP;
        }
    }
}
