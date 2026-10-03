namespace SeatHive.Api.Models
{
    public static class Roles
    {
        public const string User = "User";
        public const string Admin = "Admin";

        // The racers of the race simulation. Nobody can sign in as one.
        public const string Racer = "Racer";

        // A visitor without an account of their own (see GuestAccounts). May do what a User may.
        public const string Guest = "Guest";
    }
}
