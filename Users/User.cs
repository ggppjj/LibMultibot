using System;

namespace LibMultibot.Users;

public class User(ulong id)
{
    public ulong Id { get; } = id;
}
