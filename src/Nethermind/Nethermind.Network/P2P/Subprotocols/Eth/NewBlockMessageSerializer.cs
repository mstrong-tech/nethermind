﻿/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.Core;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public class NewBlockMessageSerializer : IMessageSerializer<NewBlockMessage>
    {
        private BlockDecoder _blockDecoder = new BlockDecoder();
        
        public byte[] Serialize(NewBlockMessage message)
        {
            int contentLength = _blockDecoder.GetLength(message.Block, RlpBehaviors.None) + Rlp.LengthOf((UInt256)message.TotalDifficulty);
            int totalLength = Rlp.LengthOfSequence(contentLength);
            RlpStream rlpStream = new RlpStream(totalLength);
            rlpStream.StartSequence(contentLength);
            rlpStream.Encode(message.Block);
            rlpStream.Encode((UInt256)message.TotalDifficulty);
            return rlpStream.Data;
        }

        public NewBlockMessage Deserialize(byte[] bytes)
        {
            Rlp.DecoderContext context = bytes.AsRlpContext();
            NewBlockMessage message = new NewBlockMessage();
            context.ReadSequenceLength();
            message.Block = Rlp.Decode<Block>(context);
            message.TotalDifficulty = context.DecodeUBigInt();
            return message;
        }
    }
}