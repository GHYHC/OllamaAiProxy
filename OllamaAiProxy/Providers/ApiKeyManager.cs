using System.Collections.Concurrent;

namespace OllamaAiProxy.Providers;

/// <summary>
/// 管理同一 provider 的多个 ApiKey，支持 429 自动标记不可用并轮换到下一个可用 Key。
/// 所有 Key 都被标记不可用后，最后一个标记的 Key 会被恢复以返回真实的 429 错误。
/// </summary>
public sealed class ApiKeyManager
{
    private readonly IReadOnlyList<string> _keys;
    private readonly ConcurrentDictionary<string, bool> _keyBlocked;
    private int _index;

    public ApiKeyManager(IReadOnlyList<string> keys)
    {
        if (keys.Count == 0)
            throw new ArgumentException("At least one API key is required.", nameof(keys));

        _keys = keys;
        _keyBlocked = new ConcurrentDictionary<string, bool>();
        _index = 0;
    }

    public int KeyCount => _keys.Count;

    /// <summary>
    /// 获取当前可用的 Key，若所有 Key 都被标记为不可用则返回第一个 Key（使调用方能收到真实 429）。
    /// </summary>
    public string GetCurrentKey()
    {
        var current = _keys[_index];
        if (!_keyBlocked.ContainsKey(current))
            return current;

        // 检查是否所有 Key 都被标记了
        if (_keys.All(k => _keyBlocked.ContainsKey(k)))
        {
            // 恢复当前 Key 以便调用方能收到真实的上游错误
            _keyBlocked.TryRemove(current, out _);
            return current;
        }

        // 轮换到下一个未标记的 Key
        return TryMoveToNextAvailable();
    }

    /// <summary>
    /// 标记当前 Key 为不可用（遇到 429 时调用），并切换到下一个 Key。
    /// 返回下一个可用的 Key，如果所有 Key 都标记了则返回 false。
    /// </summary>
    public (string Key, bool HasAvailable) MarkCurrentBlocked()
    {
        var blockedKey = _keys[_index];
        _keyBlocked[blockedKey] = true;

        return TryMoveToNext();
    }

    /// <summary>
    /// 重置所有 Key 的 429 标记（例如经过冷却时间后）。
    /// </summary>
    public void ResetAll()
    {
        _keyBlocked.Clear();
    }

    private (string Key, bool HasAvailable) TryMoveToNext()
    {
        for (int i = 0; i < _keys.Count; i++)
        {
            _index = (_index + 1) % _keys.Count;
            if (!_keyBlocked.ContainsKey(_keys[_index]))
                return (_keys[_index], true);
        }

        // 所有 Key 都被标记了，重置并返回第一个
        _keyBlocked.Clear();
        _index = 0;
        return (_keys[0], false);
    }

    private string TryMoveToNextAvailable()
    {
        for (int i = 0; i < _keys.Count; i++)
        {
            _index = (_index + 1) % _keys.Count;
            if (!_keyBlocked.ContainsKey(_keys[_index]))
                return _keys[_index];
        }

        // 所有 Key 都被标记了，重置并返回当前
        _keyBlocked.Clear();
        return _keys[_index];
    }
}
