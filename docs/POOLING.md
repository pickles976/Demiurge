Basic example:
  // "Despawn" without removing from the scene — hides the model,
  // stops scripts/lights, but keeps the entity allocated and positioned.
  entity.EnableAll(false, applyOnChildren: true);

  // "Respawn" — reactivate everything.
  entity.EnableAll(true, applyOnChildren: true);

  Pooling example (matching your SpawnGun spawn-by-Scene pattern):
  private readonly Queue<Entity> _gunPool = new();
  
  private Entity SpawnGun(Entity owner)
  {
      Entity gun;
      if (_gunPool.Count > 0)
      {
          gun = _gunPool.Dequeue();
          gun.Transform.Position = owner.Transform.Position;
          gun.EnableAll(true, applyOnChildren: true);   // wake it back up
      }   
      else
      {
          gun = new Entity { /* model, script, etc. */ };
          gun.Scene = Entity.Scene;                      // spawn into the scene once
      }
      return gun;
  }
  
  private void DespawnGun(Entity gun)
  {
      gun.EnableAll(false, applyOnChildren: true);       // disable, don't destroy
      _gunPool.Enqueue(gun);
  }