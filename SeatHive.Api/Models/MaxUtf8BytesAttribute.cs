using System.ComponentModel.DataAnnotations;
using System.Text;

namespace SeatHive.Api.Models
{
    // Limits a string by its UTF-8 size, not by its character count.
    public class MaxUtf8BytesAttribute : ValidationAttribute
    {
        private readonly int _maxBytes;

        public MaxUtf8BytesAttribute(int maxBytes)
        {
            _maxBytes = maxBytes;
            ErrorMessage = $"The field {{0}} must be at most {maxBytes} bytes long.";
        }

        public override bool IsValid(object? value)
        {
            return value is not string text || Encoding.UTF8.GetByteCount(text) <= _maxBytes;
        }
    }
}
