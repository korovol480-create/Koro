using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR;

// Attach to one empty GameObject. Builds the whole world at runtime.
public class SandboxGameLogic : MonoBehaviour
{
    [Header("Gun")]
    public float gunFireInterval = 0.1f;
    public float gunImpulse = 60f;
    public float gunDamage = 20f;
    public Vector3 gunHoldEuler = Vector3.zero;

    [Header("Knife")]
    public float knifeMinSpeed = 1.0f;
    public float knifeCutSpeed = 3.5f;
    public float knifeForceScale = 6f;
    public float knifeDamagePerSpeed = 12f;
    public Vector3 knifeHoldEuler = Vector3.zero;

    [Header("NPC")]
    public int maxNpcs = 4;

    // ---------- data ----------
    class PartInfo { public NpcData npc; public Rigidbody rb; public CharacterJoint joint; public string name; public float nextHit; }
    class NpcData { public GameObject root; public List<PartInfo> parts = new List<PartInfo>(); public float health = 100f; public bool active; }
    class Weapon
    {
        public bool gun; public Transform t; public Rigidbody rb; public Collider solid;
        public Transform muzzle, bBase, bTip; public Hand holder; public float nextFire;
        public Vector3 pBase, pTip; public bool hasPrev; public Vector3 home; public Quaternion homeRot;
    }
    class Hand
    {
        public bool right; public XRNode node; public Transform t;
        public InputAction pos, rot, grip, trig;
        public Vector3 prevWorld, vel; public Weapon held; public bool gripDown;
    }

    readonly Dictionary<Collider, PartInfo> partLookup = new Dictionary<Collider, PartInfo>();
    readonly List<NpcData> npcs = new List<NpcData>();
    readonly List<Weapon> weapons = new List<Weapon>();
    readonly Collider[] buf = new Collider[32];
    Hand left, right;
    Transform rig, btn;
    float btnCool, btnPressedUntil;
    InputAction headPos, headRot;

    static readonly Color Concrete = new Color(0.45f, 0.45f, 0.46f);
    static readonly Color DarkConcrete = new Color(0.25f, 0.25f, 0.27f);
    static readonly Color Mannequin = new Color(0.78f, 0.78f, 0.75f);

    // ---------- setup ----------
    void Awake()
    {
        Time.fixedDeltaTime = 1f / 72f;
        Physics.defaultSolverIterations = 10;
        BuildWorld();
    }

    void Start() { StartCoroutine(SetFloorOrigin()); }

    IEnumerator SetFloorOrigin()
    {
        yield return new WaitForSeconds(1f);
        var list = new List<XRInputSubsystem>();
        SubsystemManager.GetSubsystems(list);
        foreach (var s in list) s.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor);
    }

    InputAction Act(string name, InputActionType type, string path)
    {
        var a = new InputAction(name, type, path);
        a.Enable();
        return a;
    }

    GameObject Prim(PrimitiveType type, string n, Transform parent, Vector3 lp, Vector3 scale, Color c, bool collider)
    {
        var g = GameObject.CreatePrimitive(type);
        g.name = n;
        if (!collider) Destroy(g.GetComponent<Collider>());
        g.transform.SetParent(parent, false);
        g.transform.localPosition = lp;
        g.transform.localScale = scale;
        g.GetComponent<Renderer>().material.color = c;
        return g;
    }

    void BuildWorld()
    {
        // light
        var lg = new GameObject("Sun");
        var light = lg.AddComponent<Light>();
        light.type = LightType.Directional; light.intensity = 1.1f;
        lg.transform.rotation = Quaternion.Euler(50, -30, 0);
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.5f, 0.5f, 0.52f);

        // rig + camera
        rig = new GameObject("XR Rig").transform;
        headPos = Act("headPos", InputActionType.Value, "<XRHMD>/centerEyePosition");
        headRot = Act("headRot", InputActionType.Value, "<XRHMD>/centerEyeRotation");
        var camGo = new GameObject("Main Camera");
        camGo.SetActive(false);
        camGo.tag = "MainCamera";
        camGo.transform.SetParent(rig, false);
        camGo.transform.localPosition = new Vector3(0, 1.6f, 0);
        var cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.12f, 0.12f, 0.13f);
        cam.nearClipPlane = 0.05f; cam.farClipPlane = 200f;
        camGo.AddComponent<AudioListener>();
        var tpd = camGo.AddComponent<TrackedPoseDriver>();
        tpd.positionInput = new InputActionProperty(headPos);
        tpd.rotationInput = new InputActionProperty(headRot);
        camGo.SetActive(true);

        left = MakeHand(false);
        right = MakeHand(true);

        // brutalist environment
        Prim(PrimitiveType.Cube, "Floor", null, new Vector3(0, -0.05f, 0), new Vector3(30, 0.1f, 30), DarkConcrete, true);
        Prim(PrimitiveType.Cube, "Wall", null, new Vector3(0, 2f, 7f), new Vector3(14, 4, 0.6f), Concrete, true);
        Prim(PrimitiveType.Cube, "Block1", null, new Vector3(-3.5f, 1.5f, 4f), new Vector3(1.6f, 3, 1.2f), Concrete, true);
        Prim(PrimitiveType.Cube, "Block2", null, new Vector3(4f, 1f, 3.5f), new Vector3(2f, 2, 1f), Concrete, true);
        Prim(PrimitiveType.Cube, "Block3", null, new Vector3(-6f, 2.5f, 0f), new Vector3(1f, 5, 4f), Concrete, true);

        // table
        Prim(PrimitiveType.Cube, "TableTop", null, new Vector3(0, 0.8f, 0.7f), new Vector3(1.6f, 0.06f, 0.6f), Concrete, true);
        for (int i = 0; i < 4; i++)
        {
            float x = (i % 2 == 0 ? -1 : 1) * 0.72f, z = 0.7f + (i < 2 ? -1 : 1) * 0.24f;
            Prim(PrimitiveType.Cube, "Leg", null, new Vector3(x, 0.385f, z), new Vector3(0.08f, 0.77f, 0.08f), DarkConcrete, true);
        }

        // spawner button (poke with a controller)
        var b = Prim(PrimitiveType.Cube, "SpawnButton", null, new Vector3(0, 0.85f, 0.55f), new Vector3(0.18f, 0.04f, 0.18f), new Color(0.85f, 0.1f, 0.1f), false);
        btn = b.transform;

        // weapons
        weapons.Add(MakeGun(new Vector3(-0.45f, 0.9f, 0.7f)));
        weapons.Add(MakeKnife(new Vector3(0.45f, 0.9f, 0.7f)));

        // a few props
        for (int i = 0; i < 3; i++)
        {
            var c = Prim(PrimitiveType.Cube, "Crate", null, new Vector3(-1f + i * 1f, 0.25f + i * 0.01f, 1.8f), Vector3.one * 0.4f, new Color(0.55f, 0.5f, 0.42f), true);
            var rb = c.AddComponent<Rigidbody>(); rb.mass = 3f;
        }

        SpawnNpc();
    }

    Hand MakeHand(bool isRight)
    {
        string side = isRight ? "{RightHand}" : "{LeftHand}";
        var h = new Hand { right = isRight, node = isRight ? XRNode.RightHand : XRNode.LeftHand };
        var go = Prim(PrimitiveType.Sphere, isRight ? "RightHand" : "LeftHand", rig, Vector3.zero, Vector3.one * 0.07f, new Color(0.9f, 0.75f, 0.65f), false);
        Prim(PrimitiveType.Cube, "CtrlBody", go.transform, new Vector3(0, 0, -0.6f), new Vector3(0.7f, 0.5f, 2.2f), new Color(0.1f, 0.1f, 0.1f), false);
        h.t = go.transform;
        h.pos = Act("pos", InputActionType.Value, "<XRController>" + side + "/devicePosition");
        h.rot = Act("rot", InputActionType.Value, "<XRController>" + side + "/deviceRotation");
        h.grip = Act("grip", InputActionType.Value, "<XRController>" + side + "/grip");
        h.trig = Act("trig", InputActionType.Value, "<XRController>" + side + "/trigger");
        return h;
    }

    Weapon MakeGun(Vector3 pos)
    {
        var root = new GameObject("Gun");
        root.transform.position = pos;
        Prim(PrimitiveType.Cube, "Body", root.transform, new Vector3(0, 0.02f, 0.05f), new Vector3(0.04f, 0.07f, 0.2f), new Color(0.12f, 0.12f, 0.13f), false);
        Prim(PrimitiveType.Cube, "Handle", root.transform, new Vector3(0, -0.05f, -0.02f), new Vector3(0.035f, 0.1f, 0.045f), new Color(0.2f, 0.2f, 0.2f), false);
        var muzzle = new GameObject("Muzzle").transform;
        muzzle.SetParent(root.transform, false);
        muzzle.localPosition = new Vector3(0, 0.02f, 0.16f);
        var col = root.AddComponent<BoxCollider>();
        col.size = new Vector3(0.05f, 0.17f, 0.24f); col.center = new Vector3(0, -0.02f, 0.04f);
        var rb = root.AddComponent<Rigidbody>();
        rb.mass = 0.8f; rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        return new Weapon { gun = true, t = root.transform, rb = rb, solid = col, muzzle = muzzle, home = pos, homeRot = root.transform.rotation };
    }

    Weapon MakeKnife(Vector3 pos)
    {
        var root = new GameObject("Knife");
        root.transform.position = pos;
        Prim(PrimitiveType.Cube, "Handle", root.transform, new Vector3(0, 0, 0), new Vector3(0.03f, 0.03f, 0.1f), new Color(0.15f, 0.1f, 0.08f), false);
        Prim(PrimitiveType.Cube, "Blade", root.transform, new Vector3(0, 0, 0.13f), new Vector3(0.01f, 0.04f, 0.16f), new Color(0.8f, 0.8f, 0.85f), false);
        var bb = new GameObject("BladeBase").transform; bb.SetParent(root.transform, false); bb.localPosition = new Vector3(0, 0, 0.06f);
        var bt = new GameObject("BladeTip").transform; bt.SetParent(root.transform, false); bt.localPosition = new Vector3(0, 0, 0.21f);
        var col = root.AddComponent<BoxCollider>();
        col.size = new Vector3(0.04f, 0.045f, 0.32f); col.center = new Vector3(0, 0, 0.1f);
        var rb = root.AddComponent<Rigidbody>();
        rb.mass = 0.3f; rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        return new Weapon { gun = false, t = root.transform, rb = rb, solid = col, bBase = bb, bTip = bt, home = pos, homeRot = root.transform.rotation };
    }

    // ---------- ragdoll NPC ----------
    PartInfo MakePart(NpcData npc, string n, Vector3 lp, Vector3 size, float mass, PartInfo parent, Vector3 anchor, bool sphere = false)
    {
        var go = new GameObject(n);
        go.transform.SetParent(npc.root.transform, false);
        go.transform.localPosition = lp;
        var rb = go.AddComponent<Rigidbody>();
        rb.mass = mass; rb.drag = 0.3f; rb.angularDrag = 0.8f; rb.solverIterations = 12;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        Collider col;
        if (sphere) { var sc = go.AddComponent<SphereCollider>(); sc.radius = size.x * 0.5f; col = sc; }
        else { var bc = go.AddComponent<BoxCollider>(); bc.size = size; col = bc; }
        Prim(sphere ? PrimitiveType.Sphere : PrimitiveType.Cube, "Vis", go.transform, Vector3.zero, size, Mannequin, false);

        CharacterJoint j = null;
        if (parent != null)
        {
            j = go.AddComponent<CharacterJoint>();
            j.connectedBody = parent.rb;
            j.anchor = anchor;
            j.axis = Vector3.up;
            j.swingAxis = Vector3.forward;
            j.lowTwistLimit = new SoftJointLimit { limit = -30f };
            j.highTwistLimit = new SoftJointLimit { limit = 30f };
            j.swing1Limit = new SoftJointLimit { limit = 40f };
            j.swing2Limit = new SoftJointLimit { limit = 40f };
            j.enableProjection = true;
        }
        rb.isKinematic = true; // stands frozen until first hit
        var info = new PartInfo { npc = npc, rb = rb, joint = j, name = n };
        partLookup[col] = info;
        npc.parts.Add(info);
        return info;
    }

    void SpawnNpc()
    {
        while (npcs.Count >= maxNpcs) { DestroyNpc(npcs[0]); npcs.RemoveAt(0); }
        var n = new NpcData();
        n.root = new GameObject("Mannequin");
        n.root.transform.position = new Vector3(Random.Range(-0.4f, 0.4f), 0f, 2.6f);
        n.root.transform.rotation = Quaternion.Euler(0, 180, 0);

        var pelvis = MakePart(n, "Pelvis", new Vector3(0, 0.95f, 0), new Vector3(0.30f, 0.18f, 0.18f), 10f, null, Vector3.zero);
        var torso = MakePart(n, "Torso", new Vector3(0, 1.30f, 0), new Vector3(0.34f, 0.50f, 0.18f), 15f, pelvis, new Vector3(0, -0.25f, 0));
        MakePart(n, "Head", new Vector3(0, 1.68f, 0), Vector3.one * 0.24f, 4f, torso, new Vector3(0, -0.12f, 0), true);
        for (int i = 0; i < 2; i++)
        {
            float s = i == 0 ? -1f : 1f;
            var ua = MakePart(n, "UpperArm" + i, new Vector3(s * 0.27f, 1.40f, 0), new Vector3(0.10f, 0.30f, 0.10f), 2.5f, torso, new Vector3(0, 0.15f, 0));
            MakePart(n, "LowerArm" + i, new Vector3(s * 0.27f, 1.09f, 0), new Vector3(0.09f, 0.30f, 0.09f), 1.5f, ua, new Vector3(0, 0.15f, 0));
            var th = MakePart(n, "Thigh" + i, new Vector3(s * 0.10f, 0.70f, 0), new Vector3(0.14f, 0.42f, 0.14f), 6f, pelvis, new Vector3(0, 0.21f, 0));
            MakePart(n, "Shin" + i, new Vector3(s * 0.10f, 0.27f, 0), new Vector3(0.12f, 0.42f, 0.12f), 4f, th, new Vector3(0, 0.21f, 0));
        }
        npcs.Add(n);
    }

    void DestroyNpc(NpcData n)
    {
        var dead = new List<Collider>();
        foreach (var kv in partLookup) if (kv.Value.npc == n) dead.Add(kv.Key);
        foreach (var c in dead) partLookup.Remove(c);
        Destroy(n.root);
    }

    void Activate(NpcData n)
    {
        n.active = true;
        foreach (var p in n.parts) { p.rb.isKinematic = false; p.rb.WakeUp(); }
    }

    void ApplyHit(PartInfo p, Vector3 point, Vector3 impulse, float dmg, bool sever)
    {
        if (p.rb == null) return;
        if (!p.npc.active) Activate(p.npc);
        p.rb.AddForceAtPosition(impulse, point, ForceMode.Impulse);
        p.npc.health -= dmg;
        if (sever && p.joint != null && p.name != "Pelvis") { Destroy(p.joint); p.joint = null; }
    }

    // ---------- main loop ----------
    void Update()
    {
        UpdateHand(left);
        UpdateHand(right);
        foreach (var w in weapons)
        {
            if (w.holder != null)
            {
                if (w.gun && w.holder.trig.ReadValue<float>() > 0.6f && Time.time >= w.nextFire) Fire(w);
                if (!w.gun) UpdateKnife(w);
            }
            else if (w.t.position.y < -3f)
            {
                w.rb.velocity = Vector3.zero; w.rb.angularVelocity = Vector3.zero;
                w.t.SetPositionAndRotation(w.home, w.homeRot);
            }
        }
        UpdateButton();
    }

    void UpdateHand(Hand h)
    {
        Vector3 p = h.pos.ReadValue<Vector3>();
        Quaternion q = h.rot.ReadValue<Quaternion>();
        if (q.x == 0 && q.y == 0 && q.z == 0 && q.w == 0) // no device (editor/debug)
        { p = new Vector3(h.right ? 0.25f : -0.25f, 1.1f, 0.3f); q = Quaternion.identity; }
        h.t.localPosition = p;
        h.t.localRotation = q;

        float dt = Mathf.Max(Time.deltaTime, 0.001f);
        h.vel = Vector3.Lerp(h.vel, (h.t.position - h.prevWorld) / dt, 0.5f);
        h.prevWorld = h.t.position;

        float g = h.grip.ReadValue<float>();
        if (!h.gripDown && g > 0.7f) { h.gripDown = true; TryGrab(h); }
        else if (h.gripDown && g < 0.3f) { h.gripDown = false; Drop(h); }
    }

    void TryGrab(Hand h)
    {
        if (h.held != null) return;
        Weapon best = null; float bd = 0.2f;
        foreach (var w in weapons)
        {
            if (w.holder != null) continue;
            float d = Vector3.Distance(w.t.position, h.t.position);
            if (d < bd) { bd = d; best = w; }
        }
        if (best == null) return;
        best.holder = h; h.held = best;
        best.rb.isKinematic = true;
        best.solid.enabled = false;
        best.t.SetParent(h.t, false);
        best.t.localPosition = Vector3.zero;
        best.t.localRotation = Quaternion.Euler(best.gun ? gunHoldEuler : knifeHoldEuler);
        best.hasPrev = false;
        Buzz(h, 0.3f, 0.05f);
    }

    void Drop(Hand h)
    {
        var w = h.held;
        if (w == null) return;
        w.t.SetParent(null);
        w.rb.isKinematic = false;
        w.solid.enabled = true;
        w.rb.velocity = h.vel;
        w.rb.angularVelocity = Vector3.zero;
        w.holder = null; h.held = null; w.hasPrev = false;
    }

    // ---------- gun ----------
    void Fire(Weapon w)
    {
        w.nextFire = Time.time + gunFireInterval;
        Vector3 o = w.muzzle.position, d = w.muzzle.forward;
        Vector3 end = o + d * 50f;
        RaycastHit hit;
        if (Physics.Raycast(o, d, out hit, 100f, ~0, QueryTriggerInteraction.Ignore))
        {
            end = hit.point;
            PartInfo p;
            if (partLookup.TryGetValue(hit.collider, out p)) ApplyHit(p, hit.point, d * gunImpulse, gunDamage, false);
            else if (hit.rigidbody != null && !hit.rigidbody.isKinematic) hit.rigidbody.AddForceAtPosition(d * gunImpulse, hit.point, ForceMode.Impulse);
            Sparks(hit.point, hit.normal);
        }
        // muzzle flash
        var f = Prim(PrimitiveType.Sphere, "Flash", null, o, Vector3.one * 0.06f, new Color(1f, 0.85f, 0.3f), false);
        Destroy(f, 0.04f);
        // tracer
        var tr = Prim(PrimitiveType.Cube, "Tracer", null, (o + end) * 0.5f, new Vector3(0.006f, 0.006f, Vector3.Distance(o, end)), new Color(1f, 0.9f, 0.5f), false);
        tr.transform.rotation = Quaternion.LookRotation(end - o);
        Destroy(tr, 0.03f);
        Buzz(w.holder, 0.6f, 0.04f);
    }

    void Sparks(Vector3 pt, Vector3 n)
    {
        for (int i = 0; i < 8; i++)
        {
            var s = Prim(PrimitiveType.Cube, "Spark", null, pt, Vector3.one * 0.012f, new Color(1f, 0.8f, 0.2f), false);
            var rb = s.AddComponent<Rigidbody>();
            rb.mass = 0.01f;
            rb.velocity = (n + Random.insideUnitSphere * 0.8f).normalized * Random.Range(1.5f, 4f);
            Destroy(s, 0.5f);
        }
    }

    // ---------- knife ----------
    void UpdateKnife(Weapon w)
    {
        Vector3 b = w.bBase.position, t = w.bTip.position;
        if (w.hasPrev)
        {
            Vector3 c = (b + t) * 0.5f, pc = (w.pBase + w.pTip) * 0.5f;
            Vector3 vel = (c - pc) / Mathf.Max(Time.deltaTime, 0.001f);
            float speed = vel.magnitude;
            if (speed > knifeMinSpeed)
            {
                bool stab = Vector3.Dot(vel.normalized, (t - b).normalized) > 0.75f;
                for (int i = 1; i <= 4; i++)
                {
                    float k = i / 4f;
                    Vector3 sb = Vector3.Lerp(w.pBase, b, k), st = Vector3.Lerp(w.pTip, t, k);
                    int cnt = Physics.OverlapCapsuleNonAlloc(sb, st, 0.02f, buf, ~0, QueryTriggerInteraction.Ignore);
                    for (int j = 0; j < cnt; j++)
                    {
                        PartInfo p;
                        if (!partLookup.TryGetValue(buf[j], out p) || Time.time < p.nextHit) continue;
                        p.nextHit = Time.time + 0.2f;
                        Vector3 pt = buf[j].ClosestPoint(st);
                        bool sever = !stab && speed > knifeCutSpeed;
                        ApplyHit(p, pt, vel * knifeForceScale * (stab ? 1.5f : 1f), speed * knifeDamagePerSpeed, sever);
                        Sparks(pt, -vel.normalized);
                        Buzz(w.holder, 0.7f, 0.08f);
                    }
                }
            }
        }
        w.pBase = b; w.pTip = t; w.hasPrev = true;
    }

    // ---------- spawner button ----------
    void UpdateButton()
    {
        bool touching = false;
        foreach (var h in new[] { left, right })
        {
            Vector3 l = btn.InverseTransformPoint(h.t.position);
            if (Mathf.Abs(l.x) < 0.6f && Mathf.Abs(l.z) < 0.6f && l.y > 0.1f && l.y < 2.0f)
            {
                touching = true;
                if (Time.time > btnCool)
                {
                    btnCool = Time.time + 0.8f;
                    btnPressedUntil = Time.time + 0.2f;
                    SpawnNpc();
                    Buzz(h, 0.6f, 0.1f);
                }
            }
        }
        var s = btn.localScale;
        s.y = (touching || Time.time < btnPressedUntil) ? 0.02f : 0.04f;
        btn.localScale = s;
        btn.position = new Vector3(btn.position.x, 0.83f +
