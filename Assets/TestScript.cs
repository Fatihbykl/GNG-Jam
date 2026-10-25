using UnityEngine;
using System.Collections;
using UnityEngine.SceneManagement;

public class TestScript : MonoBehaviour
{
    public void StartGame()
    {
        SceneManager.LoadScene("Assets/_Game/Scenes/Game.unity");
    }
    

    public void QuitGame()
    {
        Application.Quit();
    }

    void Start()
    {
        StartCoroutine(num());
    }

    IEnumerator num()
    {
        yield return new WaitForSeconds(0.1f);
        Time.timeScale = 0f;
    }


}
