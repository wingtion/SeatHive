namespace SeatHive.Api.Models
{
    public class User
    {
        public int Id { get; set; }
        public string Email { get; set; } = string.Empty;

        public string PasswordHash { get; set; } = string.Empty;

        public string Role { get; set; } = Roles.User;

        // UTC. Left unset, the database fills in the time of the insert.
        public DateTime CreatedAt { get; set; }
    }
}