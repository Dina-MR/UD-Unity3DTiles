using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class AudioPlayer : MonoBehaviour
{
    public AudioSource sound;

    private void Start()
    {
        sound = gameObject.GetComponent<AudioSource>();
    }

    private void OnMouseEnter()
    {
        Debug.Log("Zone trouvé ");
    }

    private void OnMouseOver()
    {
        Debug.Log("Zone détectée :" + gameObject.name);
        // On exécute le son si on clique sur le point d'intérêt
        if (Input.GetMouseButtonDown(0))
            if (sound.clip != null && !sound.isPlaying)
                sound.Play();
        // On exécute le son si on clique sur le point d'intérêt
        if (Input.GetMouseButtonDown(1))
            if (sound.clip != null)
                sound.Stop();
    }
}
