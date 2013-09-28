﻿namespace ExamSystem

module CommunicationProtocol =

    open System
    open System.IO
    open System.Text
    open System.Net
    open System.Net.Sockets
    open FSharpx.Strings    


    type ConnectionType = 
        | Control
        | Room of int
        | Unknown of string

    /// Listens on a tcp client and returns a seq<byte[]> of all
    /// found data
    let rec private listenOnClient (client:TcpClient) = 
        seq {            
            let stream = client.GetStream()

            let bytes = Array.create 4096 (byte 0)
            let read = stream.Read(bytes, 0, 4096)
            if read > 0 then 
                yield bytes.[0..read - 1]
            yield! listenOnClient client
        }    

    /// Listens on a tcp client and returns a seq<byte[]> of all
    /// found data
    let rec private readNBytes n (client:TcpClient) = 
        seq {            
            if n > 0 then 
                let stream = client.GetStream()

                let bytes = Array.create n (byte 0)
                let read = stream.Read(bytes, 0, n)
                if read > 0 then 
                    yield bytes.[0..read - 1]
                yield! readNBytes (n - read) client
        } 

    let private toArr       = Seq.concat >> Seq.toArray
    let private endianArr   = toArr >> Array.rev
    let private toInt32 a   = System.BitConverter.ToInt32(a, 0)
    let private toUInt32 a  = System.BitConverter.ToUInt32(a, 0)
    let private toInt16 a   = System.BitConverter.ToInt16(a, 0)
    let private toUInt16 a  = System.BitConverter.ToUInt16(a, 0)


    let private prepend a b = Array.append b a 
    let private readN n client = client |> readNBytes n |> endianArr

    let private num4Bytes client = client |> readN 4
    let private num2Bytes client = client |> readN 2

    let readInt32 client    = client num4Bytes |> toInt32
    let read3ByteInt client = client |> readNBytes 3 |> toArr |> prepend [|(byte 0)|] |> Array.rev |> toInt32
    let readInt16 client    = client |> num2Bytes |> toInt16
    let readUInt32 client   = client |> num4Bytes |> toInt32
    let readUInt16 client   = client |> num2Bytes |> toInt16
    let readByte client     = client |> readNBytes 1 |> toArr |> Seq.head


    let header client = client |> readNBytes 9 |> toArr |> System.Text.ASCIIEncoding.UTF8.GetString    

    let isConnected (client:TcpClient) = 
        let worker() = 
            //printfn "Checking socket connectivity"
            try
                if client.Client.Poll(10, SelectMode.SelectWrite) && not <| client.Client.Poll(0, SelectMode.SelectError)  then                    
                    let checkConn = Array.create 1 (byte 0)
                    if client.Client.Receive(checkConn, SocketFlags.Peek) = 0 then
                        false
                    else 
                        true
                else
                    false
            with
                | exn -> false

        async { return worker() }
                    
    let packets client : seq<string> = 
        seq {
                let builder = new StringBuilder()
                for str in client |> listenOnClient |> Seq.map System.Text.ASCIIEncoding.UTF8.GetString do
                    let l = (builder.ToString() + str).Split([|'\r'; '\n'|]) 
                    builder.Clear() |> ignore
                    if Seq.last l = String.Empty then
                        for entry in l |> Seq.filter ((<>) String.Empty) do yield entry
                    else
                        let nonEmpties = l |> Seq.filter ((<>) String.Empty)
                        builder.Append (Seq.last nonEmpties) |> ignore
                        for entry in (Seq.take (Seq.length nonEmpties - 1) nonEmpties) do 
                            yield entry
        }

    type Packet = 
        struct         
             val RoomId: int
             val Request: int
        end
    
    let (|IsControl|_|) str = if str = "control//" then Some(IsControl) else None
    let (|IsRoom|_|) (str:string) = 
        try
            if str.StartsWith("room/") then 
                str.Replace("room/","").Trim() |> System.Convert.ToInt32 |> Some
            else None
        with 
            | exn -> None

    let (|AdvanceCmd|_|) (str:string) = 
        if str.StartsWith("advance ") then 
            str.Replace("advance ","").Trim() |> Convert.ToInt32 |> Some
        else None

    let (|ReverseCmd|_|) (str:string) = 
        if str.StartsWith("reverse ") then 
            str.Replace("reverse ","").Trim() |> Convert.ToInt32 |> Some
        else None
    /// Checks the header sequence to see 
    /// to see if this client should be the control
    /// or if its a room. Header sequence is 9 bytes
    /// of the form : "room/#   " or "control//"
    let connectionType client =     
        match header client with 
            | IsControl -> ConnectionType.Control
            | IsRoom num -> ConnectionType.Room num 
            | str -> ConnectionType.Unknown str