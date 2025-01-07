# RSMatrix

For now this is just a little sideproject to support the matrix.org chat protocol in .NET.
I tried several libraries and got none of them to work as they were all still in beta or abandoned.

Implementing this using the [Client spec version 1.13](https://spec.matrix.org/v1.13/client-server-api/)

The goal is to have a simple text client which can retrieve and send text messages for Chatbot development. It's not intended as a comprehensive library for building a client UI.

I will try to make it conform to all requirements. It sends automatic receipts and read notifications.

End-to-End Encryption is a bonus, not sure if it will eventually be supported.