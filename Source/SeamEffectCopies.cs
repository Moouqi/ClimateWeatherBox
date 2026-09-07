using System;
using System.Collections.Generic;
using UnityEngine;

namespace ClimateWeather;

// Render-only proxies. Never instantiate a BaseEffect or copy gameplay scripts.
internal sealed class SeamEffectCopies : IDisposable
{
    private const int MaximumCopies = 128;
    private readonly List<SpriteRenderer> _pool = new List<SpriteRenderer>();
    private readonly List<SpriteRenderer> _children = new List<SpriteRenderer>();
    private readonly MaterialPropertyBlock _properties = new MaterialPropertyBlock();
    private int _used;
    internal int Count => _used;
    internal bool BudgetReached { get; private set; }

    internal void Prepare(float minY, float maxY)
    {
        Hide(); _used = 0; BudgetReached = false;
        var stack = World.world?.stack_effects;
        if (stack?.list == null || MapBox.width <= 0) return;
        int inspected = 0;
        // Clouds and their independent shadows have priority over other effects.
        for (int pass = 0; pass < 2; pass++)
        foreach (BaseEffectController controller in stack.list)
        {
            if (controller == null || (controller.asset?.id == "fx_cloud") != (pass == 0)) continue;
            int count = Math.Min(controller.getActiveIndex(), controller._list.Count);
            for (int i = 0; i < count; i++)
            {
                if (++inspected > 2048 || _used >= MaximumCopies) { BudgetReached = true; return; }
                BaseEffect effect = controller._list[i];
                if (effect == null || !effect.active || effect.state == 3) continue;
                if (effect is Cloud cloud)
                {
                    CopyAtSeam(cloud.sprite_renderer, minY, maxY);
                    CopyAtSeam(cloud.spriteShadow?.sprRndShadow, minY, maxY);
                }
                else
                {
                    _children.Clear();
                    effect.GetComponentsInChildren<SpriteRenderer>(false, _children);
                    foreach (SpriteRenderer renderer in _children) CopyAtSeam(renderer, minY, maxY);
                }
            }
        }
    }

    private void CopyAtSeam(SpriteRenderer source, float minY, float maxY)
    {
        if (source == null || !source.enabled || !source.gameObject.activeInHierarchy || source.sprite == null) return;
        Bounds bounds = source.bounds;
        if (bounds.max.y < minY || bounds.min.y > maxY) return;
        if (bounds.min.x < 0f && bounds.max.x > -MapBox.width) Copy(source, MapBox.width);
        if (bounds.max.x > MapBox.width && bounds.min.x < 2f * MapBox.width) Copy(source, -MapBox.width);
    }

    private void Copy(SpriteRenderer source, float shift)
    {
        if (_used >= MaximumCopies) { BudgetReached = true; return; }
        SpriteRenderer proxy;
        if (_used == _pool.Count)
        {
            var go = new GameObject("ClimateSeamVisualOnly");
            go.hideFlags = HideFlags.DontSave;
            proxy = go.AddComponent<SpriteRenderer>();
            proxy.enabled = false;
            _pool.Add(proxy);
        }
        else proxy = _pool[_used];
        _used++;
        proxy.gameObject.layer = source.gameObject.layer;
        proxy.sprite = source.sprite;
        proxy.sharedMaterial = source.sharedMaterial;
        proxy.color = source.color;
        proxy.flipX = source.flipX; proxy.flipY = source.flipY;
        proxy.drawMode = source.drawMode; proxy.size = source.size;
        proxy.sortingLayerID = source.sortingLayerID; proxy.sortingOrder = source.sortingOrder;
        proxy.maskInteraction = source.maskInteraction;
        proxy.spriteSortPoint = source.spriteSortPoint;
        proxy.transform.SetPositionAndRotation(source.transform.position + new Vector3(shift,0,0), source.transform.rotation);
        proxy.transform.localScale = source.transform.lossyScale;
        _properties.Clear(); source.GetPropertyBlock(_properties); proxy.SetPropertyBlock(_properties);
        proxy.enabled = true;
    }

    internal void Hide()
    {
        foreach (SpriteRenderer proxy in _pool) if (proxy != null) proxy.enabled = false;
    }

    public void Dispose()
    {
        Hide();
        foreach (SpriteRenderer proxy in _pool) if (proxy != null) UnityEngine.Object.Destroy(proxy.gameObject);
        _pool.Clear(); _children.Clear(); _used = 0;
    }
}
