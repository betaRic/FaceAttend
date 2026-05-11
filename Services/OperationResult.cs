using System;

namespace FaceAttend.Services
{
    public class OperationResult<T>
    {
        public bool Success { get; set; }
        public T Data { get; set; }
        public string ErrorCode { get; set; }
        public string Message { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        public static OperationResult<T> Ok(T data, string message = null)
        {
            return new OperationResult<T>
            {
                Success = true,
                Data = data,
                Message = message
            };
        }

        public static OperationResult<T> Fail(string errorCode, string message)
        {
            return new OperationResult<T>
            {
                Success = false,
                ErrorCode = errorCode,
                Message = message
            };
        }

        public static implicit operator bool(OperationResult<T> result)
        {
            return result?.Success ?? false;
        }
    }

    public class OperationResult
    {
        public bool Success { get; set; }
        public string ErrorCode { get; set; }
        public string Message { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        public static OperationResult Ok(string message = null)
        {
            return new OperationResult
            {
                Success = true,
                Message = message
            };
        }

        public static OperationResult Fail(string errorCode, string message)
        {
            return new OperationResult
            {
                Success = false,
                ErrorCode = errorCode,
                Message = message
            };
        }

        public static implicit operator bool(OperationResult result)
        {
            return result?.Success ?? false;
        }
    }
}
